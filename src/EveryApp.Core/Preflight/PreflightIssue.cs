namespace EveryApp.Core.Preflight;

public enum PreflightCode
{
    PathTooLong,
    ReservedName,
    TrailingDotOrSpace,
    IllegalCharForDestination,
    CaseCollision,
    TooLargeForFat32,
    NotEnoughSpace,

    /// <summary>The free space on the destination could not be read, so the space check could not run.</summary>
    FreeSpaceUnknown,

    /// <summary>Never produced by <see cref="PreflightRunner"/>; the copier reports it when it meets a locked file.</summary>
    Locked,
}

/// <summary>One problem found before copying. <paramref name="Message"/> is plain language for non-technical people.</summary>
public sealed record PreflightIssue(PreflightCode Code, string Path, string Message);
