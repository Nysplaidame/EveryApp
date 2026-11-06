using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.FileSystem;

/// <summary>
/// Real-disk <see cref="IFileSystem"/> built on File.OpenHandle and RandomAccess, with Win32 only where the BCL has no equivalent.
/// Internally every path is a full path in the <c>\\?\</c> form, so names with trailing dots or spaces keep their identity.
/// </summary>
public sealed class WindowsFileSystem(AlignedBufferPool? pool = null) : IFileSystem
{
    internal const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
    private const int DefaultSectorSize = 4096;
    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;
    private const int DirectoryBufferSize = 64 * 1024;

    private readonly AlignedBufferPool _pool = pool ?? AlignedBufferPool.Shared;
    private readonly ConcurrentDictionary<uint, int> _sectorByVolume = new();

    public IReadHandle OpenRead(string path, bool unbuffered)
    {
        try
        {
            var p = Normalize(path);
            var options = FileOptions.Asynchronous | (unbuffered ? NoBuffering : FileOptions.None);

            // Share fully so files that are open elsewhere can still be read; later tasks detect changes.
            var handle = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, options);
            try
            {
                return new WindowsReadHandle(handle, path, unbuffered ? SectorFor(handle, p) : 0, _pool);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preallocate);
        try
        {
            var p = Normalize(path);
            var options = FileOptions.Asynchronous | (unbuffered ? NoBuffering | FileOptions.WriteThrough : FileOptions.None);

            // CreateNew: never truncate or replace something that already exists.
            var handle = File.OpenHandle(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, options);
            try
            {
                if (preallocate > 0)
                {
                    ReserveAllocation(handle, preallocate); // reserves disk space only; end of file stays at 0
                }

                return new WindowsWriteHandle(handle, path, unbuffered ? SectorFor(handle, p) : 0, _pool);
            }
            catch
            {
                handle.Dispose();
                TryDeleteOwnFile(p); // we created it a moment ago; don't leave an empty stub behind
                throw;
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public FileEntry GetEntry(string path)
    {
        try
        {
            return QueryEntry(Normalize(path));
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public IEnumerable<FileEntry> EnumerateChildren(string directory)
    {
        // Open the directory eagerly so failures to open it surface here; children are then read lazily from this single handle.
        SafeFileHandle handle;
        string dir;
        uint serial;
        try
        {
            dir = Normalize(directory);
            handle = OpenQuery(dir, FileListDirectory | FileReadAttributes);
            if (!PInvoke.GetFileInformationByHandle(handle, out var info))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            serial = info.dwVolumeSerialNumber;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(directory, ex);
        }

        return Iterate(handle, dir, directory, serial);
    }

    public void CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(Normalize(path));
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public void Move(string source, string destination, bool overwrite)
    {
        try
        {
            var f = Normalize(source);
            var t = Normalize(destination);
            if (Directory.Exists(f))
            {
                Directory.Move(f, t); // never overwrites
            }
            else
            {
                File.Move(f, t, overwrite);
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(source, ex);
        }
    }

    public void Delete(string path)
    {
        try
        {
            var p = Normalize(path);
            var attrs = File.GetAttributes(p); // throws NotFound when missing
            if (attrs.HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(p, recursive: false); // removes a junction/symlink itself, never follows it
            }
            else
            {
                File.Delete(p);
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc)
    {
        try
        {
            var p = Normalize(path);
            File.SetCreationTimeUtc(p, creationUtc);
            File.SetLastWriteTimeUtc(p, lastWriteUtc);
            File.SetAttributes(p, attributes);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public long GetFreeSpace(string path)
    {
        try
        {
            var probe = ExistingAncestor(Normalize(path));
            return TryGetFreeBytes(probe, out var free)
                ? free
                : throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public int GetSectorSize(string path)
    {
        try
        {
            var probe = ExistingAncestor(Normalize(path));
            using var handle = OpenQuery(probe, FileReadAttributes);
            return SectorFor(handle, probe);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    internal static bool IsTranslatable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception;

    /// <summary>
    /// Turns a caller-supplied path into a full <c>\\?\</c> path. GetFullPath runs once and only on paths that are not already in
    /// that form (it would strip trailing dots and spaces). Device paths and anything but a drive or UNC path are rejected.
    /// </summary>
    internal static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new FileSystemException(path.Replace("\0", string.Empty, StringComparison.Ordinal), IoErrorKind.InvalidName, null);
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var rest = path.AsSpan(4);
            var isDrive = rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\';
            var isUnc = rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) && rest.Length > 4;
            return isDrive || isUnc ? path : throw new FileSystemException(path, IoErrorKind.InvalidName, null);
        }

        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new FileSystemException(path, IoErrorKind.InvalidName, null);
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (NotSupportedException ex)
        {
            throw new FileSystemException(path, IoErrorKind.InvalidName, ex);
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>The path handed back to callers: the friendly form when it round-trips, the <c>\\?\</c> form when stripping it would change the name.</summary>
    private static string Display(string longForm)
    {
        var plain = StripPrefix(longForm);
        try
        {
            return string.Equals(Path.GetFullPath(plain), plain, StringComparison.Ordinal) ? plain : longForm;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return longForm;
        }
    }

    private static string ExistingAncestor(string longForm)
    {
        var current = longForm;
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static void TryDeleteOwnFile(string longForm)
    {
        try
        {
            File.Delete(longForm);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more we can do; the original failure is what the caller needs to see.
        }
    }

    private static unsafe bool TryGetFreeBytes(string longForm, out long free)
    {
        var dir = Directory.Exists(longForm) ? longForm : Path.GetDirectoryName(longForm) ?? longForm;
        var withSlash = dir.EndsWith('\\') ? dir : dir + "\\";
        var ok = PInvoke.GetDiskFreeSpaceEx(withSlash, out var available, out _, out _);
        free = (long)Math.Min(available, long.MaxValue);
        return ok;
    }

    private static SafeFileHandle OpenQuery(string longForm, uint access, bool openReparsePoint = false)
    {
        var flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS;
        if (openReparsePoint)
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;
        }

        var handle = PInvoke.CreateFile(
            longForm,
            access,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            flags,
            null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    private static unsafe void ReserveAllocation(SafeFileHandle handle, long bytes)
    {
        long allocationSize = bytes; // FILE_ALLOCATION_INFO
        if (!PInvoke.SetFileInformationByHandle(new HANDLE(handle.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileAllocationInfo, &allocationSize, sizeof(long)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Alignment for unbuffered I/O: the larger of the logical and the physical-for-performance sector size, cached per volume.</summary>
    private int SectorFor(SafeFileHandle handle, string longForm)
    {
        uint serial = 0;
        var haveSerial = PInvoke.GetFileInformationByHandle(handle, out var info);
        if (haveSerial)
        {
            serial = info.dwVolumeSerialNumber;
            if (_sectorByVolume.TryGetValue(serial, out var cached))
            {
                return cached;
            }
        }

        var size = QueryStorageSector(handle);
        if (size == 0)
        {
            // Write-only handles may lack attribute access; ask about the directory that holds the file instead.
            var parent = Path.GetDirectoryName(longForm);
            if (parent is not null)
            {
                try
                {
                    using var dir = OpenQuery(parent, FileReadAttributes);
                    size = QueryStorageSector(dir);
                }
                catch (Win32Exception)
                {
                    size = 0;
                }
            }
        }

        if (size == 0)
        {
            return DefaultSectorSize;
        }

        if (haveSerial)
        {
            _sectorByVolume[serial] = size;
        }

        return size;
    }

    private static unsafe int QueryStorageSector(SafeFileHandle handle)
    {
        // FILE_STORAGE_INFO: LogicalBytesPerSector at 0, PhysicalBytesPerSectorForPerformance at 8.
        byte* buffer = stackalloc byte[32];
        if (!PInvoke.GetFileInformationByHandleEx(new HANDLE(handle.DangerousGetHandle()), FILE_INFO_BY_HANDLE_CLASS.FileStorageInfo, buffer, 32))
        {
            return 0;
        }

        var logical = *(uint*)buffer;
        var physical = *(uint*)(buffer + 8);
        var size = Math.Max(logical, physical);
        return size is >= 512 and <= 65536 && uint.IsPow2(size) ? (int)size : 0;
    }

    private static unsafe FileEntry QueryEntry(string longForm)
    {
        using var handle = OpenQuery(longForm, 0, openReparsePoint: true);
        if (!PInvoke.GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var attrs = (FileAttributes)info.dwFileAttributes;
        return new FileEntry(
            Display(longForm),
            (long)(((ulong)info.nFileSizeHigh << 32) | info.nFileSizeLow),
            attrs,
            ToUtc(info.ftCreationTime),
            ToUtc(info.ftLastWriteTime),
            attrs.HasFlag(FileAttributes.ReparsePoint),
            ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow,
            info.dwVolumeSerialNumber);

        static DateTime ToUtc(FILETIME ft) =>
            DateTime.FromFileTimeUtc(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime);
    }

    private static IEnumerable<FileEntry> Iterate(SafeFileHandle handle, string dirLongForm, string originalDirectory, uint serial)
    {
        using (handle)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(DirectoryBufferSize);
            try
            {
                var restart = true;
                var extended = true;
                var prefix = dirLongForm.EndsWith('\\') ? dirLongForm : dirLongForm + "\\";
                while (true)
                {
                    var error = FillDirectoryBuffer(handle, buffer, restart, extended);
                    if (error == ErrorNoMoreFiles)
                    {
                        yield break;
                    }

                    if (error == ErrorInvalidParameter && extended && restart)
                    {
                        extended = false; // older file systems: fall back to the 64-bit id layout
                        continue;
                    }

                    if (error != 0)
                    {
                        throw ErrorKinds.Translate(originalDirectory, new Win32Exception(error));
                    }

                    restart = false;
                    var offset = 0;
                    while (true)
                    {
                        var entry = ParseEntry(buffer, offset, extended, prefix, serial, out var next);
                        if (entry is not null)
                        {
                            yield return entry;
                        }

                        if (next == 0)
                        {
                            break;
                        }

                        offset += next;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static unsafe int FillDirectoryBuffer(SafeFileHandle handle, byte[] buffer, bool restart, bool extended)
    {
        var infoClass = extended
            ? (restart ? FILE_INFO_BY_HANDLE_CLASS.FileIdExtdDirectoryRestartInfo : FILE_INFO_BY_HANDLE_CLASS.FileIdExtdDirectoryInfo)
            : (restart ? FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryRestartInfo : FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo);
        fixed (byte* p = buffer)
        {
            return PInvoke.GetFileInformationByHandleEx(new HANDLE(handle.DangerousGetHandle()), infoClass, p, (uint)buffer.Length) ? 0 : Marshal.GetLastWin32Error();
        }
    }

    // FILE_ID_EXTD_DIR_INFO: name at 88, 128-bit id at 72. FILE_ID_BOTH_DIR_INFO: name at 104, 64-bit id at 96. Common head: see offsets below.
    private static FileEntry? ParseEntry(byte[] buffer, int offset, bool extended, string dirPrefix, uint serial, out int next)
    {
        var span = buffer.AsSpan(offset);
        next = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
        var created = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
        var written = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
        var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
        var id = extended
            ? BinaryPrimitives.ReadUInt64LittleEndian(span[72..])
            : BinaryPrimitives.ReadUInt64LittleEndian(span[96..]);
        var nameStart = extended ? 88 : 104;
        var name = System.Text.Encoding.Unicode.GetString(span.Slice(nameStart, nameBytes));
        if (name is "." or "..")
        {
            return null;
        }

        return new FileEntry(
            Display(dirPrefix + name),
            length,
            attributes,
            DateTime.FromFileTimeUtc(created),
            DateTime.FromFileTimeUtc(written),
            attributes.HasFlag(FileAttributes.ReparsePoint),
            id,
            serial);
    }
}
