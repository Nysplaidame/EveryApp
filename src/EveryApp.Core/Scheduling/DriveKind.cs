namespace EveryApp.Core.Scheduling;

/// <summary>What kind of storage a set of devices is.</summary>
public enum DriveKind
{
    /// <summary>Not known (network share, unresolved path, or the query failed). Treat like a spinning drive.</summary>
    Unknown,

    /// <summary>Every disk is solid state (no seek penalty).</summary>
    SolidState,

    /// <summary>At least one disk is a spinning drive.</summary>
    Spinning,
}

/// <summary>Tells whether the devices behind a path are solid state, so the copy engine knows if copying several files at once is safe.</summary>
public interface IDriveKindProbe
{
    /// <summary>SolidState only when every device is a known SSD; Spinning if any is spinning; otherwise Unknown.</summary>
    DriveKind KindOf(DeviceSet devices);
}
