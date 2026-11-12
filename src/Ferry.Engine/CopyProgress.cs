namespace Ferry.Engine;

/// <summary>Progress of one file: <paramref name="Done"/> of <paramref name="Total"/> bytes are written.</summary>
public sealed record FileProgress(string RelativePath, long Done, long Total);

/// <summary>
/// Result of copying one file. When <paramref name="Success"/> is true the destination holds the complete data; <paramref name="Problem"/> may
/// still carry a warning (for example dates and attributes could not be kept). When false, nothing was left under the destination name.
/// <paramref name="SourceHash"/> is the hash of exactly the bytes read (null when neither verify nor checksum export is on).
/// <paramref name="PartPath"/> is the last part-file name this call chose (null if it never chose one). It is for information only: it arrives
/// after the fact, so it cannot help clean up after a crash. For that, use <see cref="FileCopyHooks.PartChosen"/>, which reports every name
/// BEFORE the file is created. The copier itself never touches a part file it did not create.
/// </summary>
public sealed record FileCopyOutcome(bool Success, byte[]? SourceHash, Problem? Problem, string? PartPath = null);

/// <summary>Optional hooks for <see cref="FileCopier.CopyAsync(string, string, CopyOptions, Jobs.PauseGate, IProgress{FileProgress}, FileCopyHooks?, CancellationToken)"/>.</summary>
public sealed class FileCopyHooks
{
    /// <summary>
    /// Called with each part-file path BEFORE that file is created (a name clash means more than one call), so a job can journal it. Return
    /// false when the name could not be recorded: the copy then fails with <see cref="CopyMessages.NotRecorded"/> and no part file is created.
    /// </summary>
    public Func<string, bool>? PartChosen { get; init; }

    /// <summary>
    /// Called once the part file is complete, flushed and the right size, just before it takes the real name, with the source hash (null when
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
}
