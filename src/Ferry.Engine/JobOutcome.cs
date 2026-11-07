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

    /// <summary>Files whose copy was read back and matched (or, on resume, found to match).</summary>
    public int Verified { get; set; }

    /// <summary>A copy failed its check, or could not be read back to check it.</summary>
    public bool VerifyFailed { get; set; }

    public bool Stopped { get; set; }

    public List<Problem> Problems { get; } = [];

    public List<PreflightIssue> Issues { get; } = [];

    /// <summary>
    /// AllVerified: verification was on, the job got past its checks, nothing failed its check or could not be read back, and every file
    /// copied this run was verified (at least one). Same-drive moves are renames that read nothing back, so they do not count as verified.
    /// </summary>
    public CopyResult ToResult(bool stopped, bool refused = false) =>
        new(Copied, Skipped, Failed, [.. Problems], [.. Issues], AllVerified: verifyOn && !refused && !VerifyFailed && Verified > 0 && Verified == Copied)
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
