using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace EveryApp.Core.Scheduling;

/// <summary>
/// Maps a path to the physical devices it depends on:
/// <list type="bullet">
/// <item>UNC paths (and drive letters mapped to a share) become <c>smb:&lt;server, lowercased&gt;</c>, so every share on one server queues together.</item>
/// <item>Local paths become <c>disk:N</c> (Windows disk number) for each disk under the volume, via GetVolumePathName, GetVolumeNameForVolumeMountPoint and IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS.</item>
/// <item>Storage Spaces virtual disks also get <c>disk:M</c> for every member physical disk (WMI, root\Microsoft\Windows\Storage). If WMI fails or is denied, only the virtual disk's own <c>disk:N</c> is used.</item>
/// <item>A volume that cannot be resolved (RAM disk, odd virtual disk) becomes <c>vol:&lt;volume GUID&gt;</c>, so it still queues with itself.</item>
/// </list>
/// Never throws. Results are cached per volume for the life of the process, so only the first lookup on a volume does any I/O.
/// </summary>
public sealed class WindowsDeviceResolver : IDeviceResolver
{
    private const int PathBuffer = 1024;
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DeviceSet> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<Dictionary<uint, uint[]>> _storageSpaces = new(LoadStorageSpaces, LazyThreadSafetyMode.ExecutionAndPublication);

    public DeviceSet Resolve(string path)
    {
        try
        {
            return ResolveCore(path);
        }
        catch (Exception)
        {
            return Fallback("unresolved:" + (path ?? string.Empty).Trim().ToLowerInvariant());
        }
    }

    private DeviceSet ResolveCore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return Fallback("unresolved:" + (path ?? string.Empty).Trim().ToLowerInvariant());
        }

        var p = path.Replace('/', '\\');

        if (TryGetUncServer(p, out var server))
        {
            return DeviceSet.Of("smb:" + server.ToLowerInvariant());
        }

        string? volumeRoot = null;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
            if (p.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                // \\?\Volume{guid}\path: already a volume name; no drive letter to look up.
                var end = p.IndexOf('\\', StringComparison.Ordinal);
                volumeRoot = @"\\?\" + (end < 0 ? p : p[..end]) + @"\";
            }
        }

        volumeRoot ??= GetVolumeRoot(Path.GetFullPath(p));
        return _volumes.GetOrAdd(volumeRoot, ResolveVolume);
    }

    /// <summary>\\server\share\... or \\?\UNC\server\share\... (either slash style). Returns the server name.</summary>
    private static bool TryGetUncServer(string p, out string server)
    {
        server = string.Empty;
        string rest;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            rest = p[8..];
        }
        else if (p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\?\", StringComparison.Ordinal) && !p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            rest = p[2..];
        }
        else
        {
            return false;
        }

        var end = rest.IndexOf('\\', StringComparison.Ordinal);
        server = (end < 0 ? rest : rest[..end]).Trim();
        return server.Length > 0;
    }

    private static string GetVolumeRoot(string fullPath)
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

    private static string EnsureTrailingSlash(string s) => s.EndsWith('\\') ? s : s + "\\";

    private DeviceSet ResolveVolume(string root)
    {
        try
        {
            if (!root.StartsWith(@"\\?\", StringComparison.Ordinal) && PInvoke.GetDriveType(root) == 4 /* DRIVE_REMOTE */)
            {
                return ResolveMappedDrive(root);
            }

            var guidPath = root.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ? root : GetVolumeGuidPath(root);
            if (guidPath is null)
            {
                return Fallback(root.ToLowerInvariant());
            }

            var disks = GetDiskNumbers(guidPath);
            if (disks.Count == 0)
            {
                return Fallback(guidPath.ToLowerInvariant());
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var members = _storageSpaces.Value;
            foreach (var disk in disks)
            {
                ids.Add("disk:" + disk.ToString(CultureInfo.InvariantCulture));
                if (members.TryGetValue(disk, out var physical))
                {
                    foreach (var member in physical)
                    {
                        ids.Add("disk:" + member.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            return new DeviceSet(ids);
        }
        catch (Exception)
        {
            return Fallback(root.ToLowerInvariant());
        }
    }

    /// <summary>A drive letter mapped to \\server\share queues with every other path on that server.</summary>
    private static DeviceSet ResolveMappedDrive(string root)
    {
        Span<char> buffer = stackalloc char[PathBuffer];
        var length = (uint)buffer.Length;
        var local = root.TrimEnd('\\');
        if (PInvoke.WNetGetConnection(local, buffer, ref length) == WIN32_ERROR.NO_ERROR
            && TryGetUncServer(buffer[..buffer.IndexOf('\0')].ToString(), out var server))
        {
            return DeviceSet.Of("smb:" + server.ToLowerInvariant());
        }

        return Fallback(root.ToLowerInvariant());
    }

    private static string? GetVolumeGuidPath(string root)
    {
        Span<char> buffer = stackalloc char[PathBuffer];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer))
        {
            return null;
        }

        var name = buffer[..buffer.IndexOf('\0')].ToString();
        return name.Length > 0 ? name : null;
    }

    /// <summary>Disk numbers under a volume (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS). Empty when the volume has no disk extents or the call fails.</summary>
    private static unsafe List<uint> GetDiskNumbers(string guidPath)
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

                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != (int)WIN32_ERROR.ERROR_MORE_DATA)
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

    /// <summary>
    /// Virtual disk number -> member physical disk numbers, from WMI. Any failure (no Storage Spaces, access denied, timeout)
    /// yields an empty map, so callers fall back to the plain disk number.
    /// </summary>
    private static Dictionary<uint, uint[]> LoadStorageSpaces()
    {
        var map = new Dictionary<uint, uint[]>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage", new ConnectionOptions { Timeout = WmiTimeout });
            scope.Connect();
            var options = new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false };
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_VirtualDisk"), options);
            using var virtualDisks = searcher.Get();
            foreach (var item in virtualDisks)
            {
                using var virtualDisk = (ManagementObject)item;
                var members = new List<uint>();
                foreach (var physical in virtualDisk.GetRelated("MSFT_PhysicalDisk"))
                {
                    using (physical)
                    {
                        if (uint.TryParse(Convert.ToString(physical["DeviceId"], CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var member))
                        {
                            members.Add(member);
                        }
                    }
                }

                foreach (var disk in virtualDisk.GetRelated("MSFT_Disk"))
                {
                    using (disk)
                    {
                        if (disk["Number"] is not null && members.Count > 0)
                        {
                            map[Convert.ToUInt32(disk["Number"], CultureInfo.InvariantCulture)] = [.. members];
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // WMI unavailable or denied: keep whatever was read, the rest falls back to disk:N only.
        }

        return map;
    }

    private static DeviceSet Fallback(string key) => DeviceSet.Of("vol:" + key);
}
