namespace EveryApp.Core.FileSystem;

/// <summary>
/// Metadata snapshot of one file or directory. <paramref name="FullPath"/> is always in the canonical long form
/// (<c>\\?\C:\...</c> or <c>\\?\UNC\server\share\...</c>); use <see cref="PathDisplay.ToDisplayPath"/> for user-facing text.
/// <paramref name="FileId"/> (128-bit) and <paramref name="VolumeSerial"/> identify the object on disk, identically from GetEntry and
/// EnumerateChildren (used to detect junction loops).
/// </summary>
public sealed record FileEntry(
    string FullPath,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    bool IsReparsePoint,
    UInt128 FileId,
    ulong VolumeSerial);
