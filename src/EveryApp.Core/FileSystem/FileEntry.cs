namespace EveryApp.Core.FileSystem;

/// <summary>Metadata snapshot of one file or directory. <paramref name="FileId"/> and <paramref name="VolumeSerial"/> identify the object on disk (used to detect junction loops).</summary>
public sealed record FileEntry(
    string FullPath,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    bool IsReparsePoint,
    ulong FileId,
    uint VolumeSerial);
