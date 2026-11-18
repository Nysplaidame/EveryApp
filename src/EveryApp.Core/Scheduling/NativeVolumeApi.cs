using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.Scheduling;

internal sealed class NativeVolumeApi : IVolumeApi
{
    private const int PathBuffer = 1024;
    private const uint DriveRemote = 4;

    public string? GetMappedDriveServer(string driveLetter)
    {
        // GetDriveType on a letter reads the drive table only; it does not contact the server.
        if (PInvoke.GetDriveType(driveLetter + "\\") != DriveRemote)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[PathBuffer];
        var length = (uint)buffer.Length;
        var error = PInvoke.WNetGetConnection(driveLetter, buffer, ref length);

        // ERROR_CONNECTION_UNAVAIL (1201): the share is offline or reconnecting, but the remote name is still filled in.
        if (error is WIN32_ERROR.NO_ERROR or WIN32_ERROR.ERROR_CONNECTION_UNAVAIL)
        {
            var end = buffer.IndexOf('\0');
            var remote = (end < 0 ? buffer : buffer[..end]).ToString().Replace('/', '\\');
            if (remote.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var rest = remote[2..];
                var slash = rest.IndexOf('\\', StringComparison.Ordinal);
                var server = (slash < 0 ? rest : rest[..slash]).Trim();
                if (server.Length > 0)
                {
                    return server;
                }
            }
        }

        return string.Empty;
    }

    public string? GetSubstTarget(string driveLetter)
    {
        Span<char> buffer = stackalloc char[PathBuffer];
        var written = PInvoke.QueryDosDevice(driveLetter, buffer);
        if (written == 0)
        {
            return null;
        }

        var end = buffer.IndexOf('\0');
        var target = (end < 0 ? buffer[..(int)written] : buffer[..end]).ToString();
        return target.StartsWith(@"\??\", StringComparison.Ordinal) ? target : null;
    }

    public string GetVolumeRoot(string fullPath)
    {
        Span<char> buffer = stackalloc char[PathBuffer];
        if (PInvoke.GetVolumePathName(fullPath, buffer))
        {
            var root = buffer[..buffer.IndexOf('\0')].ToString();
            if (root.Length > 0)
            {
                return EnsureTrailingSlash(root);
            }
        }

        return EnsureTrailingSlash(Path.GetPathRoot(fullPath) ?? fullPath);
    }

    public string? GetVolumeGuidPath(string root)
    {
        Span<char> buffer = stackalloc char[PathBuffer];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer))
        {
            return null;
        }

        var name = buffer[..buffer.IndexOf('\0')].ToString();
        return name.Length > 0 ? name : null;
    }

    public unsafe IReadOnlyList<uint> GetDiskNumbers(string guidPath)
    {
        var result = new List<uint>();

        // Opened with no access rights: enough for this query, and works without elevation.
        using var handle = PInvoke.CreateFile(
            guidPath.TrimEnd('\\'),
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            0,
            null);
        if (handle.IsInvalid)
        {
            return result;
        }

        // VOLUME_DISK_EXTENTS: DWORD count, 4 bytes padding, then DISK_EXTENT[count] of 24 bytes (DWORD DiskNumber, padding, offset, length).
        var size = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                uint returned = 0;
                var added = false;
                bool ok;
                try
                {
                    handle.DangerousAddRef(ref added);
                    ok = PInvoke.DeviceIoControl(new HANDLE(handle.DangerousGetHandle()), PInvoke.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, p, (uint)size, &returned, null);
                }
                finally
                {
                    if (added)
                    {
                        handle.DangerousRelease();
                    }
                }

                if (ok)
                {
                    var count = *(uint*)p;
                    for (var i = 0; i < count && 8 + ((i + 1) * 24) <= size; i++)
                    {
                        var disk = *(uint*)(p + 8 + (i * 24));
                        if (!result.Contains(disk))
                        {
                            result.Add(disk);
                        }
                    }

                    return result;
                }

                if (Marshal.GetLastWin32Error() != (int)WIN32_ERROR.ERROR_MORE_DATA)
                {
                    return result;
                }

                size = 8 + ((int)Math.Min(*(uint*)p, 4096u) * 24); // the driver reports the extent count even when the buffer is too small
                if (size < 1032)
                {
                    size = 8192;
                }
            }
        }

        return result;
    }

    private static string EnsureTrailingSlash(string s) => s.EndsWith('\\') ? s : s + "\\";
}
