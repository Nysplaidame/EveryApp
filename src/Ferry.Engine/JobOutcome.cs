using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine;

/// <summary>Counters and lists that become the <see cref="CopyResult"/>. One job, one thread at a time.</summary>
internal sealed class JobOutcome(bool verifyOn, int maxProblems = JobOutcome.DefaultMaxProblems)
{
    public const int DefaultMaxProblems = 10_000;

    private bool _ownerWarned;
    private bool _notLocalWarned;

    public int Copied { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    /// <summary>Files whose copy was read back and matched (or, on resume, found to match).</summary>
    public int Verified { get; set; }

    /// <summary>A copy failed its check, or could not be read back to check it.</summary>
    public bool VerifyFailed { get; set; }

    public bool Stopped { get; set; }

    /// <summary>Every problem, but only the first <c>maxProblems</c> are kept (the rest are counted), so a hostile tree cannot exhaust memory.</summary>
    public ProblemList Problems { get; } = new(maxProblems);

    public List<PreflightIssue> Issues { get; } = [];

    /// <summary>
    /// AllVerified: verification was on, the job got past its checks, nothing failed its check or could not be read back, and every file
    /// copied this run was verified (at least one). Same-drive moves are renames that read nothing back, so they do not count as verified.
    /// </summary>
    public CopyResult ToResult(bool stopped, bool refused = false) =>
        new(Copied, Skipped, Failed, Problems.Items, [.. Issues], AllVerified: verifyOn && !refused && !VerifyFailed && Verified > 0 && Verified == Copied)
        {
            Stopped = stopped,
            MoreProblems = Problems.More,
        };

    /// <summary>A job that was refused before it started: one plain reason, nothing done.</summary>
    public static CopyResult Refused(string path, string message) =>
        new(0, 0, 0, [new Problem(path, IoErrorKind.None, message)], [], AllVerified: false) { Stopped = true };

    /// <summary>How copying permissions went for one item. Advice that applies to the whole job is given once.</summary>
    public void PermissionOutcome(string target, SecurityCopyResult result)
    {
        if (result == SecurityCopyResult.OwnerNotKept && !_ownerWarned)
        {
            _ownerWarned = true;
            Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.AccessDenied, CopyMessages.OwnerNotKept));
        }
        else if (result == SecurityCopyResult.Reverted)
        {
            Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.AccessDenied, CopyMessages.PermissionsReverted));
        }
    }

    /// <summary>Permissions could not be copied at all for one item (the data is fine).</summary>
    public void PermissionProblem(string target, FileSystemException ex) =>
        Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, CopyMessages.PermissionsNotCopied));

    /// <summary>Permissions are only copied between fixed local drives; said once per job.</summary>
    public void PermissionsNotLocal(string path)
    {
        if (!_notLocalWarned)
        {
            _notLocalWarned = true;
            Problems.Add(new Problem(TreePaths.Display(path), IoErrorKind.None, CopyMessages.PermissionsNotLocal));
        }
    }
}

/// <summary>Keeps the first problems and counts the rest.</summary>
internal sealed class ProblemList(int max)
{
    private readonly List<Problem> _items = [];

    public int Count => _items.Count + More;

    public int More { get; private set; }

    public IReadOnlyList<Problem> Items => [.. _items];

    public void Add(Problem problem)
    {
        if (_items.Count < max)
        {
            _items.Add(problem);
        }
        else
        {
            More++;
        }
    }

    public void AddRange(IEnumerable<Problem> problems)
    {
        foreach (var p in problems)
        {
            Add(p);
        }
    }
}

/// <summary>Writes journal records for a job (nothing at all before the journal exists, e.g. during checks).</summary>
internal sealed class JobJournalWriter(JobJournal? journal)
{
    public JobJournal? Journal { get; set; } = journal;

    public void Append(JournalRecord record)
    {
        try
        {
            Journal?.Append(record);
        }
        catch (ArgumentException)
        {
            // A name the journal cannot hold safely: the file is simply redone on resume (copies never land half-written).
        }
    }

    /// <summary>
    /// Appends a record that MUST be in the journal before the caller goes on (a part-file name). False when the journal refused it (an unsafe
    /// name, or a journal that failed earlier): the caller must then not create the file.
    /// </summary>
    public bool TryAppend(JournalRecord record)
    {
        if (Journal is null)
        {
            return true;
        }

        try
        {
            Journal.Append(record);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Done(string key, long length, DateTime lastWrite, string? hash) =>
        Append(new JournalRecord(JournalRecordKind.FileDone, key, length, lastWrite, hash, null));
}
