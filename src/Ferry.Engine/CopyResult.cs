using EveryApp.Core.FileSystem;
using EveryApp.Core.Preflight;

namespace Ferry.Engine;

public enum CopyMode
{
    Copy,
    Move,
}

/// <summary>
/// Progress of a whole job. <paramref name="Phase"/> is one of "Checking" (listing and pre-flight checks), "Waiting" (another job is using the
/// same drive), "Copying" and "Verifying" (reading a copy back to check it). <paramref name="BytesPerSecond"/> and <paramref name="Remaining"/>
/// come from a 10-second moving average; <paramref name="Remaining"/> is null until there is a speed to go on.
/// </summary>
public sealed record JobProgress(
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    double BytesPerSecond,
    TimeSpan? Remaining,
    string? CurrentFile,
    string Phase);

/// <summary>
/// Outcome of a job. <paramref name="Copied"/> counts files copied (or moved) this run, <paramref name="Skipped"/> files left alone by a
/// conflict rule, <paramref name="Failed"/> files that could not be copied. <paramref name="Problems"/> lists every failure and warning in
/// plain language. When <paramref name="PreflightIssues"/> holds a blocking issue nothing was written. <paramref name="AllVerified"/> is true
/// only when verification was on and every file copied this run was read back and matched (same-drive moves are renames, so their data
/// never changed and counts as checked).
/// </summary>
public sealed record CopyResult(
    int Copied,
    int Skipped,
    int Failed,
    IReadOnlyList<Problem> Problems,
    IReadOnlyList<PreflightIssue> PreflightIssues,
    bool AllVerified)
{
    /// <summary>For a dry run, what would happen to each folder and file, in order. Empty otherwise.</summary>
    public IReadOnlyList<PlannedItem> Plan { get; init; } = [];

    /// <summary>True when the job stopped early because of a problem (failure rule Stop) or a blocking pre-flight issue.</summary>
    public bool Stopped { get; init; }

    /// <summary>Problems beyond the first <see cref="JobOutcome.DefaultMaxProblems"/>, counted but not listed (keeps memory bounded on hostile trees).</summary>
    public int MoreProblems { get; init; }
}

/// <summary>What a dry run says would happen to one item.</summary>
public enum PlannedAction
{
    CreateFolder,
    Copy,
    Move,
    Replace,
    Skip,

    /// <summary>The copy gets a new name such as "name (2).ext" (rules Rename and KeepBoth); the existing file is left alone.</summary>
    CopyWithNewName,

    /// <summary>The conflict rule is Ask: the person will be asked.</summary>
    Ask,
}

/// <summary>One line of a dry-run plan. Paths are for display.</summary>
public sealed record PlannedItem(string Source, string Destination, PlannedAction Action);

/// <summary>What a resume will do, shown to the person before it runs. The saved job is untrusted input, so it is never resumed unasked.</summary>
public sealed record ResumeRequest(IReadOnlyList<string> Sources, string Destination, CopyMode Mode);

/// <summary>Asks the person to confirm a resume (the CLI shows sources, destination and mode, or takes --yes).</summary>
public interface IResumeConfirmation
{
    ValueTask<bool> ConfirmAsync(ResumeRequest request, CancellationToken ct);
}

/// <summary>Decides what to do when a file already exists at the destination and the job's rule is <see cref="ConflictRule.Ask"/>.</summary>
public interface IConflictResolver
{
    /// <summary>Returns the rule to apply to this one file. Returning <see cref="ConflictRule.Ask"/> again is treated as Skip.</summary>
    ValueTask<ConflictRule> ResolveAsync(FileEntry source, FileEntry existing, CancellationToken ct);
}
