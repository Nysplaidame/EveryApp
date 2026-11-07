using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.FileSystem;

/// <summary>
/// Real-disk <see cref="IFileSystem"/> built on File.OpenHandle and RandomAccess, with Win32 only where the BCL has no equivalent.
/// Internally (and in every <see cref="FileEntry.FullPath"/>) paths are full paths in the <c>\\?\</c> form, so names with trailing dots
/// or spaces keep their identity.
/// </summary>
public sealed class WindowsFileSystem(AlignedBufferPool? pool = null) : IFileSystem
{
    internal const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
    internal static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
    private const int DefaultSectorSize = 4096;
    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorHandleEof = 38;
    private const int DirectoryBufferSize = 64 * 1024;

    private readonly AlignedBufferPool _pool = pool ?? AlignedBufferPool.Shared;
    private readonly ConcurrentDictionary<ulong, int> _sectorByVolume = new();

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
                    // End of file is set up front (extending writes are slow and serialised on NTFS). The target is a temporary part-file
                    // that nobody trusts until it is verified and renamed; the file is trimmed to the data written on flush/dispose.
                    RandomAccess.SetLength(handle, preallocate);
                }

                return new WindowsWriteHandle(handle, path, unbuffered ? SectorFor(handle, p) : 0, _pool, preallocate > 0);
            }
            catch
            {
                handle.Dispose();
                TryDeleteOwnFile(p); // we created it a moment ago; don't leave a stub behind
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

    public FileEntry GetEntryFollowingLinks(string path)
    {
        try
        {
            return QueryEntry(Normalize(path), followLinks: true);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    public IEnumerable<FileEntry> EnumerateChildren(string directory)
    {
        // Probe now so a directory that cannot be opened fails here; every enumeration then opens its own handle.
        string dir;
        try
        {
            dir = Normalize(directory);
            using var probe = OpenDirectory(dir, out _);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(directory, ex);
        }

        return Iterate(dir, directory);
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

    public void CopySecurity(string source, string target)
    {
        try
        {
            var f = Normalize(source);
            var t = Normalize(target);
            var isDirectory = Directory.Exists(f);
            var original = ReadSecurity(f, isDirectory, AccessControlSections.Access | AccessControlSections.Owner);

            // The access list first (explicit entries plus "block inheritance"); inherited entries are recomputed from the new parent.
            var access = isDirectory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
            access.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            WriteSecurity(t, isDirectory, access);

            // The owner only when it differs: setting someone else as owner needs the restore privilege (administrator).
            var owner = original.GetOwner(typeof(SecurityIdentifier));
            var current = ReadSecurity(t, isDirectory, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (owner is not null && !owner.Equals(current))
            {
                var ownerOnly = isDirectory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
                ownerOnly.SetOwner(owner);
                WriteSecurity(t, isDirectory, ownerOnly);
            }
        }
        catch (Exception ex) when (ex is PrivilegeNotHeldException or InvalidOperationException)
        {
            // Not allowed to set that owner: only an administrator can.
            throw new FileSystemException(target, IoErrorKind.AccessDenied, ex);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(target, ex);
        }
    }

    public void CopyAlternateStreams(string source, string target)
    {
        try
        {
            var f = Normalize(source);
            var t = Normalize(target);
            foreach (var stream in NamedStreams(f))
            {
                using var reader = File.OpenHandle(f + stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var writer = File.OpenHandle(t + stream, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    long offset = 0;
                    int n;
                    while ((n = RandomAccess.Read(reader, buffer, offset)) > 0)
                    {
                        RandomAccess.Write(writer, buffer.AsSpan(0, n), offset);
                        offset += n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(target, ex);
        }
    }

    public bool TryCloneBlocks(string source, string target)
    {
        string f;
        string t;
        try
        {
            f = Normalize(source);
            t = Normalize(target);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            return false;
        }

        SafeFileHandle? output = null;
        var created = false;
        var done = false;
        try
        {
            using var input = File.OpenHandle(f, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = RandomAccess.GetLength(input);

            // Fails on anything but ReFS, so other file systems stop here before anything is created.
            if (length == 0 || !BlockClone.TryGetIntegrity(input, out var integrity))
            {
                return false;
            }

            output = File.OpenHandle(t, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            created = true;
            if (File.GetAttributes(f).HasFlag(FileAttributes.SparseFile) && !BlockClone.SetSparse(output))
            {
                return false;
            }

            // Cloning needs both files to use the same integrity-stream setting.
            if (!BlockClone.SetIntegrity(output, integrity))
            {
                return false;
            }

            RandomAccess.SetLength(output, length);
            done = BlockClone.Duplicate(input, output, length, integrity.ClusterSize) && BlockClone.Flush(output);
            return done;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            return false;
        }
        finally
        {
            output?.Dispose();
            if (created && !done)
            {
                TryDeleteOwnFile(t); // we created it in this call; never leave a half-cloned file
            }
        }
    }

    internal static bool IsTranslatable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception;

    /// <summary>
    /// Turns a caller-supplied path into a full <c>\\?\</c> path. A drive-rooted or UNC path without dot segments is prefixed as it is
    /// (GetFullPath would strip trailing dots and spaces); only relative or dotted input goes through GetFullPath. Paths already in
    /// <c>\\?\</c> form are accepted only when they are drive or UNC paths. Device paths are rejected.
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

        if (TryPrefixDirectly(path.Replace('/', '\\'), out var direct))
        {
            return direct;
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

    private static bool TryPrefixDirectly(string path, out string result)
    {
        result = string.Empty;
        int start;
        bool unc;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            start = 3;
            unc = false;
        }
        else if (path.Length > 2 && path[0] == '\\' && path[1] == '\\' && path[2] is not ('\\' or '?' or '.'))
        {
            start = 2;
            unc = true;
        }
        else
        {
            return false;
        }

        var rest = path.AsSpan(start);
        if (rest.Contains(':'))
        {
            return false; // drive-relative colons, alternate streams: let GetFullPath decide
        }

        var segments = 0;
        while (!rest.IsEmpty)
        {
            var i = rest.IndexOf('\\');
            var segment = i < 0 ? rest : rest[..i];
            rest = i < 0 ? default : rest[(i + 1)..];
            if (segment.IsEmpty)
            {
                if (rest.IsEmpty)
                {
                    break; // a single trailing backslash
                }

                return false; // doubled backslash
            }

            if (segment is "." or "..")
            {
                return false;
            }

            segments++;
        }

        if (unc && segments < 2)
        {
            return false; // server and share are both required
        }

        result = unc ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        return true;
    }

    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
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

    private static FileSystemSecurity ReadSecurity(string longForm, bool isDirectory, AccessControlSections sections) =>
        isDirectory ? new DirectoryInfo(longForm).GetAccessControl(sections) : new FileInfo(longForm).GetAccessControl(sections);

    private static void WriteSecurity(string longForm, bool isDirectory, FileSystemSecurity security)
    {
        if (isDirectory)
        {
            new DirectoryInfo(longForm).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(longForm).SetAccessControl((FileSecurity)security);
        }
    }

    /// <summary>Suffixes such as <c>:Zone.Identifier:$DATA</c> for every named stream of a file (the unnamed main stream is left out).</summary>
    private static unsafe List<string> NamedStreams(string longForm)
    {
        var names = new List<string>();
        WIN32_FIND_STREAM_DATA data;
        HANDLE raw;
        fixed (char* name = longForm)
        {
            raw = PInvoke.FindFirstStream(name, STREAM_INFO_LEVELS.FindStreamInfoStandard, &data, 0);
        }

        if ((nint)raw.Value is -1 or 0)
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorHandleEof ? names : throw new Win32Exception(error);
        }

        int last;
        try
        {
            do
            {
                var stream = data.cStreamName.ToString();
                if (!stream.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(stream);
                }
            }
            while (PInvoke.FindNextStream(raw, &data));

            last = Marshal.GetLastWin32Error();
        }
        finally
        {
            _ = PInvoke.FindClose(raw);
        }

        return last == ErrorHandleEof ? names : throw new Win32Exception(last);
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

    private static bool TryGetFreeBytes(string longForm, out long free)
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

    private static SafeFileHandle OpenDirectory(string longForm, out ulong volumeSerial)
    {
        var handle = OpenQuery(longForm, FileListDirectory | FileReadAttributes);
        if (!NativeFileApi.TryGetFileId(handle, out volumeSerial, out _))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    /// <summary>Alignment for unbuffered I/O: the larger of the logical and the physical-for-performance sector size, cached per volume.</summary>
    private int SectorFor(SafeFileHandle handle, string longForm)
    {
        var haveSerial = NativeFileApi.TryGetFileId(handle, out var serial, out _);
        if (haveSerial && _sectorByVolume.TryGetValue(serial, out var cached))
        {
            return cached;
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
        if (!NativeFileApi.GetInfoEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileStorageInfo, buffer, 32, out _))
        {
            return 0;
        }

        var logical = *(uint*)buffer;
        var physical = *(uint*)(buffer + 8);
        var size = Math.Max(logical, physical);
        return size is >= 512 and <= 65536 && uint.IsPow2(size) ? (int)size : 0;
    }

    private static FileEntry QueryEntry(string longForm, bool followLinks = false)
    {
        using var handle = OpenQuery(longForm, FileReadAttributes, openReparsePoint: !followLinks);
        if (!PInvoke.GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!NativeFileApi.TryGetFileId(handle, out var serial, out var id))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var attrs = (FileAttributes)info.dwFileAttributes;
        return new FileEntry(
            longForm,
            (long)(((ulong)info.nFileSizeHigh << 32) | info.nFileSizeLow),
            attrs,
            ToUtc(info.ftCreationTime),
            ToUtc(info.ftLastWriteTime),
            attrs.HasFlag(FileAttributes.ReparsePoint),
            id,
            serial,
            attrs.HasFlag(FileAttributes.ReparsePoint) ? QueryReparseTag(handle) : 0);

        static DateTime ToUtc(FILETIME ft) =>
            DateTime.FromFileTimeUtc(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime);
    }

    private static unsafe uint QueryReparseTag(SafeFileHandle handle)
    {
        // FILE_ATTRIBUTE_TAG_INFO: FileAttributes at 0, ReparseTag at 4.
        byte* buffer = stackalloc byte[8];
        return NativeFileApi.GetInfoEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, buffer, 8, out _)
            ? *(uint*)(buffer + 4)
            : 0;
    }

    private static IEnumerable<FileEntry> Iterate(string dirLongForm, string originalDirectory)
    {
        SafeFileHandle handle;
        ulong serial;
        try
        {
            handle = OpenDirectory(dirLongForm, out serial); // a fresh handle for every enumeration
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(originalDirectory, ex);
        }

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
            return NativeFileApi.GetInfoEx(handle, infoClass, p, (uint)buffer.Length, out var error) ? 0 : error;
        }
    }

    // FILE_ID_EXTD_DIR_INFO: 128-bit id at 72, name at 88. FILE_ID_BOTH_DIR_INFO: 64-bit id at 96, name at 104. Common head up to offset 64.
    private static FileEntry? ParseEntry(byte[] buffer, int offset, bool extended, string dirPrefix, ulong serial, out int next)
    {
        var span = buffer.AsSpan(offset);
        next = (int)BinaryPrimitives.ReadUInt32LittleEndian(span);
        var created = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
        var written = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
        var nameBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]);
        var id = extended
            ? BinaryPrimitives.ReadUInt128LittleEndian(span[72..])
            : BinaryPrimitives.ReadUInt64LittleEndian(span[96..]);
        var nameStart = extended ? 88 : 104;
        var name = System.Text.Encoding.Unicode.GetString(span.Slice(nameStart, nameBytes));
        if (name is "." or "..")
        {
            return null;
        }

        return new FileEntry(
            dirPrefix + name,
            length,
            attributes,
            DateTime.FromFileTimeUtc(created),
            DateTime.FromFileTimeUtc(written),
            attributes.HasFlag(FileAttributes.ReparsePoint),
            id,
            serial,
            // Both layouts keep the tag in the 4 bytes after the attributes/name length: FILE_ID_EXTD_DIR_INFO.ReparsePointTag at 68,
            // FILE_ID_BOTH_DIR_INFO.EaSize at 64 (which holds the tag when the entry is a reparse point).
            attributes.HasFlag(FileAttributes.ReparsePoint)
                ? BinaryPrimitives.ReadUInt32LittleEndian(span[(extended ? 68 : 64)..])
                : 0);
    }
}
