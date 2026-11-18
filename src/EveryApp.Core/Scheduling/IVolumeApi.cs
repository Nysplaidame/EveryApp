namespace EveryApp.Core.Scheduling;

/// <summary>The few Windows volume queries the resolver needs, as a seam so the resolution rules can be tested without real drives.</summary>
internal interface IVolumeApi
{
    /// <summary>
    /// For a drive letter ("Z:") that is a mapped network drive: the server name, or "" when it is remote but the server cannot be read.
    /// Null when it is not a network drive. Must not touch the network.
    /// </summary>
    string? GetMappedDriveServer(string driveLetter);

    /// <summary>For a SUBST drive ("S:"): the target as the system reports it (starts with \??\). Null when it is not a SUBST drive.</summary>
    string? GetSubstTarget(string driveLetter);

    /// <summary>The mount point of the volume holding the path (GetVolumePathName), with a trailing backslash.</summary>
    string GetVolumeRoot(string fullPath);

    /// <summary>The volume GUID path (\\?\Volume{...}\) of a mount point, or null when it has none.</summary>
    string? GetVolumeGuidPath(string root);

    /// <summary>Windows disk numbers under a volume. Empty when it has no disk extents or the query fails.</summary>
    IReadOnlyList<uint> GetDiskNumbers(string guidPath);
}
