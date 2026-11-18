using EveryApp.Core.Scheduling;

namespace EveryApp.Core.Tests.Fakes;

/// <summary>Maps a path to a fixed set of device ids. Unmapped paths resolve to their own private device.</summary>
internal sealed class FakeDeviceResolver : IDeviceResolver
{
    private readonly Dictionary<string, string[]> _map = new(StringComparer.OrdinalIgnoreCase);

    public FakeDeviceResolver Map(string path, params string[] ids)
    {
        _map[path] = ids;
        return this;
    }

    public DeviceSet Resolve(string path) =>
        new(new HashSet<string>(_map.TryGetValue(path, out var ids) ? ids : ["private:" + path], StringComparer.Ordinal));
}
