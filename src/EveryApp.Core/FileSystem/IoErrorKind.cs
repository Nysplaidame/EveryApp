namespace EveryApp.Core.FileSystem;

/// <summary>Disk-level failure categories that the copy engine reacts to. Everything else is <see cref="Other"/>.</summary>
public enum IoErrorKind
{
    None,
    NotFound,
    AccessDenied,
    SharingViolation,
    DiskFull,
    BadBlock,
    NetworkLost,
    PathTooLong,
    InvalidName,
    Other,
}
