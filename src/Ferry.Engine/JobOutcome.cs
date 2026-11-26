using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine;

/// <summary>Counters and lists that become the <see cref="CopyResult"/>. Safe to share between the copies of one job running at once.</summary>
internal sealed class JobOutcome(bool verifyOn, int maxProblems = JobOutcome.DefaultMaxProblems)
{
    public const int DefaultMaxProblems = 10_000;

    private readonly object _gate = new();
    private int _copied;
    private int _skipped;
    private int _failed;
    private int _verified;
    private bool _verifyFailed;
    private bool _stopped;
    private bool _ownerWarned;
    private bool _notLocalWarned;

    public int Copied { get { lock (_gate) { return _copied; } } }

    public int Skipped { get { lock (_gate) { return _skipped; } } }

    public int Failed { get { lock (_gate) { return _failed; } } }

    /// <summary>Files whose copy was read back and matched (or, on resume, found to match).</summary>
    public int Verified { get { lock (_gate) { return _verified; } } }

    /// <summary>A copy failed its check, or could not be read back to check it.</summary>
    public bool VerifyFailed { get { lock (_gate) { return _verifyFailed; } } }

    public bool Stopped { get { lock (_gate) { return _stopped; } } }

    /// <summary>Counts <paramref name="n"/> more files copied.</summary>
    public void AddCopied(int n = 1)
    {
        lock (_gate)
        {
            _copied += n;
        }
    }

    /// <summary>Counts one more file skipped.</summary>
    public void AddSkipped()
    {
        lock (_gate)
        {
            _skipped++;
        }
    }

    /// <summary>Counts <paramref name="n"/> more files failed.</summary>
    public void AddFailed(int n = 1)
    {
        lock (_gate)
        {
            _failed += n;
        }
    }

    /// <summary>Counts one more file read back and matched.</summary>
    public void AddVerified()
    {
        lock (_gate)
        {
            _verified++;
        }
    }

    /// <summary>Notes that a copy failed its check or could not be read back.</summary>
    public void MarkVerifyFailed()
    {
        lock (_gate)
        {
            _verifyFailed = true;
        }
    }

    /// <summary>Notes that the job was stopped before it finished.</summary>
    public void MarkStopped()
    {
        lock (_gate)
        {
            _stopped = true;
        }
    }

    /// <summary>Every problem, but only the first <c>maxProblems</c> are kept (the rest are counted), so a hostile tree cannot exhaust memory.</summary>
    public ProblemList Problems { get; } = new(maxProblems);

    public List<PreflightIssue> Issues { get; } = [];

    /// <summary>
    /// AllVerified: verification was on, the job got past its checks, nothing failed its check or could not be read back, and every file
    /// copied this run was verified (at least one). Same-drive moves are renames that read nothing back, so they do not count as verified.
    /// </summary>
    public CopyResult ToResult(bool stopped, bool refused = false)
    {
        lock (_gate)
        {
            return new(_copied, _skipped, _failed, Problems.Items, [.. Issues], AllVerified: verifyOn && !refused && !_verifyFailed && _verified > 0 && _verified == _copied)
            {
                Stopped = stopped,
                MoreProblems = Problems.More,
            };
        }
    }

    /// <summary>A job that was refused before it started: one plain reason, nothing done.</summary>
    public static CopyResult Refused(string path, string message) =>
        new(0, 0, 0, [new Problem(path, IoErrorKind.None, message)], [], AllVerified: false) { Stopped = true };

    private bool TakeOnce(ref bool flag)
    {
        lock (_gate)
        {
            if (flag)
            {
                return false;
            }

            flag = true;
            return true;
        }
    }

    /// <summary>How copying permissions went for one item. Advice that applies to the whole job is given once.</summary>
    public void PermissionOutcome(string target, SecurityCopyResult result)
    {
        if (result == SecurityCopyResult.OwnerNotKept && TakeOnce(ref _ownerWarned))
        {
            Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.AccessDenied, CopyMessages.OwnerNotKept));
        }
        else if (result == SecurityCopyResult.Reverted)
        {
            Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.AccessDenied, CopyMessages.PermissionsReverted));
        }
        else if (result == SecurityCopyResult.StillLockedOut)
        {
            Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.AccessDenied, CopyMessages.PermissionsStillLockedOut));
        }
    }

    /// <summary>Permissions could not be copied at all for one item (the data is fine).</summary>
    public void PermissionProblem(string target, FileSystemException ex) =>
        Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, CopyMessages.PermissionsNotCopied));

    /// <summary>Permissions are only copied between fixed local drives; said once per job.</summary>
    public void PermissionsNotLocal(string path)
    {
        if (TakeOnce(ref _notLocalWarned))
        {
            Problems.Add(new Problem(TreePaths.Display(path), IoErrorKind.None, CopyMessages.PermissionsNotLocal));
        }
    }
}

/// <summary>Keeps the first problems and counts the rest. Safe to share between threads.</summary>
internal sealed class ProblemList(int max)
{
    private readonly object _gate = new();
    private readonly List<Problem> _items = [];
    private int _more;

    public int Count { get { lock (_gate) { return _items.Count + _more; } } }

    public int More { get { lock (_gate) { return _more; } } }

    public IReadOnlyList<Problem> Items { get { lock (_gate) { return [.. _items]; } } }

    public void Add(Problem problem)
    {
        lock (_gate)
        {
            if (_items.Count < max)
            {
                _items.Add(problem);
            }
            else
            {
                _more++;
            }
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

    /// <param name="onDisk">False when the file was renamed into place without being forced to disk: a resume then checks it again.</param>
    public void Done(string key, long length, DateTime lastWrite, string? hash, bool onDisk = true) =>
        Append(new JournalRecord(JournalRecordKind.FileDone, key, length, lastWrite, hash, onDisk ? null : JobJournal.NotOnDiskYet));
}
