namespace Ferry.Engine;

/// <summary>Progress of one file: <paramref name="Done"/> of <paramref name="Total"/> bytes are written.</summary>
public sealed record FileProgress(string RelativePath, long Done, long Total);

/// <summary>
/// Result of copying one file. When <paramref name="Success"/> is true the destination holds the complete data; <paramref name="Problem"/> may
/// still carry a warning (for example dates and attributes could not be kept). Rescue saves zero-filled data with a warning and
/// <see cref="FileCopyOutcome.Lost"/> ranges: Success means saved, not intact. When false, nothing was left under the destination name.
/// <paramref name="SourceHash"/> is the hash of exactly the bytes read (null when neither verify nor checksum export is on).
/// <paramref name="PartPath"/> is the last part-file name this call chose (null if it never chose one). It is for information only: it arrives
/// after the fact, so it cannot help clean up after a crash. For that, use <see cref="FileCopyHooks.PartChosen"/>, which reports every name
/// BEFORE the file is created. The copier itself never touches a part file it did not create.
/// </summary>
public sealed record FileCopyOutcome(bool Success, byte[]? SourceHash, Problem? Problem, string? PartPath = null)
{
    /// <summary>
    /// Rescue mode only: the parts of the source that could not be read and were filled with blanks (zeros) in the copy, merged and in order.
    /// When this is not empty the copy is NOT a clean success: <see cref="Problem"/> carries the warning, the copy must not count as
    /// verified, and a move must keep the original.
    /// </summary>
    public IReadOnlyList<LostRange> Lost { get; init; } = [];

    /// <summary>The copier already waited and retried (or gave up rescuing) before failing, so running the whole file again won't help.</summary>
    public bool NoRetry { get; init; }

    /// <summary>
    /// False when the data was not forced to disk before the rename (<see cref="FileCopyHooks.SkipSmallFileFlush"/>): after a power cut the
    /// file may hold blanks or old data under its real name, so a resume must check it again before trusting it.
    /// </summary>
    public bool Flushed { get; init; } = true;

    /// <summary>The copy was read back and did not match the source, or could not be read back (<see cref="FileCopyHooks.VerifyInCopier"/>).</summary>
    public bool VerifyFailed { get; init; }
}

/// <summary>Optional hooks for <see cref="FileCopier.CopyAsync(string, string, CopyOptions, Jobs.PauseGate, IProgress{FileProgress}, FileCopyHooks?, CancellationToken)"/>.</summary>
public sealed class FileCopyHooks
{
    /// <summary>
    /// Called with each part-file path BEFORE that file is created (a name clash means more than one call), so a job can journal it. Return
    /// false when the name could not be recorded: the copy then fails with <see cref="CopyMessages.NotRecorded"/> and no part file is created.
    /// </summary>
    public Func<string, bool>? PartChosen { get; init; }

    /// <summary>
    /// One speed limiter shared by every copy of a job running at once, so together they stay within the limit. When null the copier makes its
    /// own from <see cref="CopyOptions.SpeedLimitBytesPerSecond"/>.
    /// </summary>
    internal SpeedLimiter? SharedLimiter { get; init; }

    /// <summary>
    /// Called once the part file is complete, flushed (see <see cref="SkipSmallFileFlush"/>) and the right size, just before it takes the real name, with the source hash (null when
    /// neither verify nor checksum export is on). Return a <see cref="Problem"/> to refuse: the copy then fails and the part file is deleted.
    /// Used to verify the copy and to add extra data streams before the real name ever appears.
    /// </summary>
    public Func<string, byte[]?, CancellationToken, ValueTask<Problem?>>? BeforeCommit { get; init; }

    /// <summary>
    /// Replace an existing destination: the part file is renamed over it (ReadOnly is cleared first). The old file is never deleted before the
    /// new data is complete. Without this an existing destination fails the copy with <see cref="CopyMessages.AlreadyExists"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Try block cloning first (ReFS, same volume); any failure falls back to a normal copy.</summary>
    public bool TryBlockClone { get; init; }

    /// <summary>
    /// The destination root's final path (links resolved). When set, each part file's final path, read from its own handle right after it is
    /// created, must be inside it; otherwise the part is deleted through that same handle and the copy fails with
    /// <see cref="CopyMessages.PartOutsideDestination"/>. Stops a folder swapped for a link between the check and the write.
    /// </summary>
    public string? MustBeUnder { get; init; }

    /// <summary>Plain status lines while the copy waits, e.g. "Waiting for the network drive…". Called from copy threads.</summary>
    public IProgress<string>? Status { get; init; }

    /// <summary>
    /// Skip forcing the data to disk before the rename for small files (below <see cref="FileCopier.UnbufferedThresholdBytes"/>), when not in
    /// rescue mode and the destination was never reopened. Forcing each small file to disk is most of its copy time. Only for a caller that
    /// journals <see cref="FileCopyOutcome.Flushed"/> and rechecks such files on resume, and never when the source will be deleted (a move).
    /// </summary>
    public bool SkipSmallFileFlush { get; init; }

    /// <summary>
    /// With <see cref="CopyOptions.Verify"/>, the copier itself reads the copy back and compares it with the source hash before the real name
    /// appears (instead of leaving that to <see cref="BeforeCommit"/>). A small file is then written unbuffered and read back through the same
    /// handle before it closes, which avoids the antivirus wait on reopening a just-written file. A failed check comes back as
    /// <see cref="FileCopyOutcome.VerifyFailed"/>.
    /// </summary>
    public bool VerifyInCopier { get; init; }

    /// <summary>Called just before the copier starts reading the copy back (<see cref="VerifyInCopier"/>). Called from copy threads.</summary>
    public Action? Verifying { get; init; }
}
