namespace EveryApp.Core.FileSystem;

/// <summary>
/// Metadata snapshot of one file or directory. <paramref name="FullPath"/> is always in the canonical long form
/// (<c>\\?\C:\...</c> or <c>\\?\UNC\server\share\...</c>); use <see cref="PathDisplay.ToDisplayPath"/> for user-facing text.
/// <paramref name="FileId"/> (128-bit) and <paramref name="VolumeSerial"/> identify the object on disk, identically from GetEntry and
/// EnumerateChildren (used to detect junction loops).
/// <paramref name="ReparseTag"/> is the reparse tag (IO_REPARSE_TAG_*) when <paramref name="IsReparsePoint"/> is set, otherwise 0. It tells a
/// link (name surrogate: junction, symlink, mount point) from other reparse data such as cloud placeholders and deduplicated files.
/// </summary>
public sealed record FileEntry(
    string FullPath,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    bool IsReparsePoint,
    UInt128 FileId,
    ulong VolumeSerial,
    uint ReparseTag = 0);
