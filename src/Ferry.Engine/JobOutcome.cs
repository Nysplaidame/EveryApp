using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine;

/// <summary>Counters and lists that become the <see cref="CopyResult"/>. One job, one thread at a time.</summary>
internal sealed class JobOutcome(bool verifyOn)
{
    private bool _needsAdminReported;

    public int Copied { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public bool VerifyMismatch { get; set; }

    public bool Stopped { get; set; }

    public List<Problem> Problems { get; } = [];

    public List<PreflightIssue> Issues { get; } = [];

    /// <summary>AllVerified: verification was on, no copy failed its check, and the job got past its checks (a refused job verified nothing).</summary>
    public CopyResult ToResult(bool stopped, bool refused = false) =>
        new(Copied, Skipped, Failed, [.. Problems], [.. Issues], AllVerified: verifyOn && !VerifyMismatch && !refused)
        {
            Stopped = stopped,
        };

    /// <summary>A permissions failure. Missing rights get one clear piece of advice for the whole job rather than the same line for every file.</summary>
    public void PermissionProblem(string target, FileSystemException ex)
    {
        if (ex.Kind != IoErrorKind.AccessDenied)
        {
            Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, CopyMessages.PermissionsNotCopied));
        }
        else if (!_needsAdminReported)
        {
            _needsAdminReported = true;
            Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, CopyMessages.NeedsAdministrator));
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

    public void Done(string key, long length, DateTime lastWrite, string? hash) =>
        Append(new JournalRecord(JournalRecordKind.FileDone, key, length, lastWrite, hash, null));
}
