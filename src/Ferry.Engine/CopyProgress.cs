namespace Ferry.Engine;

/// <summary>Progress of one file: <paramref name="Done"/> of <paramref name="Total"/> bytes are written.</summary>
public sealed record FileProgress(string RelativePath, long Done, long Total);

/// <summary>
/// Result of copying one file. When <paramref name="Success"/> is true the destination holds the complete data; <paramref name="Problem"/> may
/// still carry a warning (for example dates and attributes could not be kept). When false, nothing was left under the destination name.
/// <paramref name="SourceHash"/> is the hash of exactly the bytes read (null when neither verify nor checksum export is on).
/// </summary>
public sealed record FileCopyOutcome(bool Success, byte[]? SourceHash, Problem? Problem);
