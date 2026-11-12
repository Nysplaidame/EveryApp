using System.Collections.Concurrent;
using System.Globalization;
using System.Management;

namespace EveryApp.Core.Scheduling;

/// <summary>
/// Maps a path to the physical devices it depends on:
/// <list type="bullet">
/// <item>UNC paths (and drive letters mapped to a share) become <c>smb:&lt;server, lowercased&gt;</c>, so every share on one server queues together. A mapped letter is recognised before any volume call, so resolving it never touches the network.</item>
/// <item>SUBST drives resolve to their backing path, so they queue with the real disk.</item>
/// <item>Local paths become <c>disk:N</c> (Windows disk number) for each disk under the volume (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS).</item>
/// <item>Storage Spaces virtual disks also get <c>disk:M</c> for every member physical disk (WMI, root\Microsoft\Windows\Storage).</item>
/// <item>A volume that cannot be resolved (RAM disk, odd virtual disk) becomes <c>vol:&lt;volume GUID&gt;</c>, so it still queues with itself.</item>
/// </list>
/// Never throws. Volume results are cached by volume GUID (not drive letter, so a different drive under the same letter is seen).
/// WMI runs on a background task and is waited on for at most a few seconds: if it is slow, down or failed, that call gets disk:N only,
/// the result is NOT cached, and WMI is retried after a 60 s back-off. A slow load that eventually finishes is picked up later.
/// </summary>
public sealed class WindowsDeviceResolver : IDeviceResolver
{
    private const int MaxIndirections = 4;
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(60);

    private readonly IVolumeApi _api;
    private readonly Func<Dictionary<uint, uint[]>> _loadStorageSpaces;
    private readonly TimeProvider _time;
    private readonly TimeSpan _mapWait;
    private readonly ConcurrentDictionary<string, DeviceSet> _volumes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _mapGate = new();
    private Dictionary<uint, uint[]>? _map; // set only after a complete, successful load
    private Task<Dictionary<uint, uint[]>>? _load;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public WindowsDeviceResolver()
        : this(new NativeVolumeApi(), LoadStorageSpaces, TimeProvider.System, TimeSpan.FromSeconds(3))
    {
    }

    internal WindowsDeviceResolver(IVolumeApi api, Func<Dictionary<uint, uint[]>> loadStorageSpaces, TimeProvider time, TimeSpan mapWait)
    {
        _api = api;
        _loadStorageSpaces = loadStorageSpaces;
        _time = time;
        _mapWait = mapWait;
    }

    public DeviceSet Resolve(string path)
    {
        try
        {
            return ResolveCore(path, 0);
        }
        catch (Exception)
        {
            return Fallback("unresolved:" + (path ?? string.Empty).Trim().ToLowerInvariant());
        }
    }

    private DeviceSet ResolveCore(string? path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal) || depth > MaxIndirections)
        {
            return Fallback("unresolved:" + (path ?? string.Empty).Trim().ToLowerInvariant());
        }

        var p = path.Replace('/', '\\');

        if (TryGetUncServer(p, out var server))
        {
            return DeviceSet.Of("smb:" + server.ToLowerInvariant());
        }

        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
            if (p.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                // \\?\Volume{guid}\path: already a volume name; no drive letter to look up.
                var end = p.IndexOf('\\', StringComparison.Ordinal);
                return ResolveVolume(@"\\?\" + (end < 0 ? p : p[..end]) + @"\");
            }
        }

        var full = Path.GetFullPath(p);

        // Drive letter first: mapped and SUBST drives are recognised before GetVolumePathName could touch the network or fail.
        if (full.Length >= 2 && full[1] == ':' && char.IsAsciiLetter(full[0]))
        {
            var letter = full[..2];
            var mapped = _api.GetMappedDriveServer(letter);
            if (mapped is not null)
            {
                return mapped.Length > 0 ? DeviceSet.Of("smb:" + mapped.ToLowerInvariant()) : Fallback("remote:" + letter.ToLowerInvariant());
            }

            var subst = _api.GetSubstTarget(letter);
            if (subst is not null)
            {
                var rest = full.Length > 3 ? full[2..] : string.Empty;
                return ResolveCore(subst.TrimEnd('\\') + rest, depth + 1);
            }
        }

        var root = _api.GetVolumeRoot(full);
        var guid = _api.GetVolumeGuidPath(root);
        return guid is null ? Fallback(root.ToLowerInvariant()) : ResolveVolume(guid);
    }

    /// <summary>\\server\share\... or \\?\UNC\server\share\... (either slash style). Returns the server name.</summary>
    private static bool TryGetUncServer(string p, out string server)
    {
        server = string.Empty;
        string rest;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            rest = p[(p.IndexOf("UNC\\", StringComparison.OrdinalIgnoreCase) + 4)..];
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

    private DeviceSet ResolveVolume(string guidPath)
    {
        if (_volumes.TryGetValue(guidPath, out var cached))
        {
            return cached;
        }

        try
        {
            var disks = _api.GetDiskNumbers(guidPath);
            if (disks.Count == 0)
            {
                return _volumes.GetOrAdd(guidPath, Fallback(guidPath.ToLowerInvariant()));
            }

            var members = GetStorageSpaces();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var disk in disks)
            {
                ids.Add("disk:" + disk.ToString(CultureInfo.InvariantCulture));
                if (members is not null && members.TryGetValue(disk, out var physical))
                {
                    foreach (var member in physical)
                    {
                        ids.Add("disk:" + member.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            var set = new DeviceSet(ids);
            return members is null ? set : _volumes.GetOrAdd(guidPath, set); // without the member map the answer may be incomplete: do not cache it
        }
        catch (Exception)
        {
            return Fallback(guidPath.ToLowerInvariant());
        }
    }

    /// <summary>The Storage Spaces member map, or null when it is not available right now (slow, failed or backing off). Never blocks longer than the wait budget.</summary>
    private Dictionary<uint, uint[]>? GetStorageSpaces()
    {
        Task<Dictionary<uint, uint[]>> load;
        lock (_mapGate)
        {
            if (_map is not null)
            {
                return _map;
            }

            var now = _time.GetUtcNow();
            if (_load is { IsCompleted: true })
            {
                var finished = _load;
                _load = null;
                if (finished.IsCompletedSuccessfully)
                {
                    return _map = finished.Result;
                }

                _ = finished.Exception; // observe it
                _retryAt = now + RetryBackoff;
                return null;
            }

            if (now < _retryAt)
            {
                return null;
            }

            load = _load ??= Task.Run(_loadStorageSpaces);
        }

        try
        {
            load.Wait(_mapWait);
        }
        catch (AggregateException)
        {
            // handled below via the task state
        }

        lock (_mapGate)
        {
            if (load.IsCompletedSuccessfully)
            {
                _load = null;
                return _map = load.Result;
            }

            if (load.IsCompleted)
            {
                _ = load.Exception;
                if (ReferenceEquals(_load, load))
                {
                    _load = null;
                }
            }

            // Failed or still running: answer without members, and do not ask again until the back-off has passed.
            _retryAt = _time.GetUtcNow() + RetryBackoff;
            return null;
        }
    }

    /// <summary>Virtual disk number -> member physical disk numbers, from WMI. Throws on any failure so the caller does not cache a partial map.</summary>
    private static Dictionary<uint, uint[]> LoadStorageSpaces()
    {
        var map = new Dictionary<uint, uint[]>();
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

        return map;
    }

    private static DeviceSet Fallback(string key) => DeviceSet.Of("vol:" + key);
}
