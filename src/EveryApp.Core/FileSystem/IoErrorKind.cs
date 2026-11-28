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
    AlreadyExists,

    /// <summary>A rename was asked to cross volumes (ERROR_NOT_SAME_DEVICE); nothing was copied or moved.</summary>
    NotSameDevice,
    Other,
}
