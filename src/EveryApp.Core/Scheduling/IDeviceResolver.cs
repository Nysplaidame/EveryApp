namespace EveryApp.Core.Scheduling;

public interface IDeviceResolver
{
    /// <summary>
    /// The devices behind a path. Never throws and never returns an empty set: a path that cannot be resolved still gets an id
    /// of its own, so it serialises with itself. The path may be plain or in the canonical \\?\ form and need not exist yet.
    /// </summary>
    DeviceSet Resolve(string path);
}
