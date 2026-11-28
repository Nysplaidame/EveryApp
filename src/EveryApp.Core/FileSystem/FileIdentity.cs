namespace EveryApp.Core.FileSystem;

/// <summary>Which object on disk an open file is: the same pair as <see cref="FileEntry.FileId"/> and <see cref="FileEntry.VolumeSerial"/>.</summary>
public readonly record struct FileIdentity(UInt128 FileId, ulong VolumeSerial);
