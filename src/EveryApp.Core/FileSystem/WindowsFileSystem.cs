using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.FileSystem;

/// <summary>Real-disk <see cref="IFileSystem"/> built on File.OpenHandle and RandomAccess, with Win32 only where the BCL has no equivalent.</summary>
public sealed class WindowsFileSystem : IFileSystem
{
    internal const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
    private const int LongPathThreshold = 259;
    private const int DefaultSectorSize = 4096;

    public IReadHandle OpenRead(string path, bool unbuffered)
    {
        try
        {
            var p = Normalize(path);
            var options = FileOptions.Asynchronous | (unbuffered ? NoBuffering : FileOptions.None);
            var handle = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.Read, options);
            try
            {
                return new WindowsReadHandle(handle, path, unbuffered ? SectorSizeOf(p) : 0);
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
            var handle = File.OpenHandle(p, FileMode.Create, FileAccess.Write, FileShare.None, options);
            try
            {
                if (preallocate > 0)
                {
                    RandomAccess.SetLength(handle, preallocate);
                }

                return new WindowsWriteHandle(handle, path, unbuffered ? SectorSizeOf(p) : 0, preallocate);
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
        try
        {
            var dir = Normalize(directory);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            };
            var names = new FileSystemEnumerable<string>(dir, static (ref FileSystemEntry e) => e.FileName.ToString(), options).ToList();
            var result = new List<FileEntry>(names.Count);
            foreach (var name in names)
            {
                result.Add(QueryEntry(Normalize(Path.Combine(StripPrefix(dir), name))));
            }

            return result;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(directory, ex);
        }
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
            var probe = ExistingAncestor(StripPrefix(Normalize(path)));
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
            return SectorSizeOf(Normalize(path));
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(path, ex);
        }
    }

    internal static bool IsTranslatable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception;

    /// <summary>Full path, with the long-path prefix added only when needed. Never prefixes a relative path.</summary>
    internal static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid path.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return full;
        }

        if (full.Length <= LongPathThreshold)
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string ExistingAncestor(string full)
    {
        var current = full;
        while (!Directory.Exists(current))
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

    private static unsafe bool TryGetFreeBytes(string directory, out long free)
    {
        var withSlash = directory.EndsWith('\\') ? directory : directory + "\\";
        ulong available;
        var ok = PInvoke.GetDiskFreeSpaceEx(withSlash, out available, out _, out _);
        free = (long)Math.Min(available, long.MaxValue);
        return ok;
    }

    private static int SectorSizeOf(string normalizedPath)
    {
        var root = Path.GetPathRoot(StripPrefix(normalizedPath));
        if (string.IsNullOrEmpty(root))
        {
            return DefaultSectorSize;
        }

        var ok = PInvoke.GetDiskFreeSpace(root, out _, out var bytesPerSector, out _, out _);
        return ok && bytesPerSector >= 512 ? (int)bytesPerSector : DefaultSectorSize;
    }

    private static unsafe FileEntry QueryEntry(string normalizedPath)
    {
        using var handle = PInvoke.CreateFile(
            normalizedPath,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!PInvoke.GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var attrs = (FileAttributes)info.dwFileAttributes;
        return new FileEntry(
            StripPrefix(normalizedPath),
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
}
