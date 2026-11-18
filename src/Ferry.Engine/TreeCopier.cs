using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Paths;
using EveryApp.Core.Preflight;
using EveryApp.Core.Scheduling;

namespace Ferry.Engine;

/// <summary>
/// Copies or moves files and folder trees into a destination folder as one resumable job.
/// <para>
/// Order: list (walk) → pre-flight checks → wait for the drive (scheduler) → keep the PC awake → create the journal → copy each file in walk
/// order (folders first, small files first within each folder) → verify each file right after its copy → finish. Nothing is written before
/// the scheduler slot is held and the journal exists; a blocking pre-flight issue or an overlap between source and destination stops the job
/// before anything is written.
/// </para>
/// <para>
/// Safety rules: the real name only ever holds complete data (and, when verify is on, data that was read back unbuffered and matched); an
/// existing destination is replaced only by renaming the finished copy over it, never deleted first; a move deletes the original only after its
/// copy matched, and only through a handle that proved it is still the same file; only part files named in the journal are ever deleted on
/// resume; links are never followed by a move; nothing is written through a folder link already in the destination, and a part file whose
/// final path leaves the destination is deleted through its own handle; the "downloaded from the internet" marker always travels with a
/// file; the engine refuses to run elevated.
/// </para>
/// <para>
/// Links: with FollowReparsePoints off, a link to a folder or to a file is skipped (and listed) when copying, never followed; with it on,
/// file links are copied as the contents they point to. Other reparse files (cloud placeholders, deduplicated files) are ordinary data. A
/// move never follows links: on the same volume a link is moved as itself; to another volume it is left in place and listed.
/// </para>
/// <para>
/// Resume and metadata: a file is journaled done only after its metadata and permissions are applied. On resume, a file that has a part
/// record but no done record, and whose destination already holds the same data (compared by hash), was renamed into place just before a
/// crash: its metadata is re-applied and it is journaled done instead of being copied again.
/// </para>
/// <para>
/// Files are processed one at a time within a job. Copying many small files in parallel is a Task 16 speed lever (it would need per-file
/// progress and journal ordering to stay correct); it is deliberately not done here.
/// </para>
/// </summary>
public sealed class TreeCopier
{
    /// <summary>Waits before each retry of a failure that might clear up (bad read, network blip, file in use).</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    internal IFileSystem FileSystem { get; }

    internal DriveAwareScheduler Scheduler { get; }

    internal IConflictResolver Resolver { get; }

    internal string JobsDir { get; }

    internal ISleepBlocker SleepBlocker { get; }

    internal IDestinationInfo DestinationInfo { get; }

    internal TimeProvider Time { get; }

    internal FileCopier Copier { get; }

    internal IElevationCheck Elevation { get; }

    public TreeCopier(IFileSystem fs, DriveAwareScheduler scheduler, IConflictResolver resolver, string jobsDir)
        : this(fs, scheduler, resolver, jobsDir, null)
    {
    }

    /// <param name="fs">Disk access.</param>
    /// <param name="scheduler">Keeps jobs on the same drive from running at once.</param>
    /// <param name="resolver">Asked about conflicts when the rule is Ask.</param>
    /// <param name="jobsDir">Folder for job journals.</param>
    /// <param name="sleepBlocker">Keeps the PC awake while a job runs (default: <see cref="WindowsSleepBlocker"/>).</param>
    /// <param name="destinationInfo">File system and long-path facts (default: <see cref="WindowsDestinationInfo"/>).</param>
    /// <param name="time">Clock for speed, retries and the journal (default: system clock).</param>
    public TreeCopier(
        IFileSystem fs,
        DriveAwareScheduler scheduler,
        IConflictResolver resolver,
        string jobsDir,
        ISleepBlocker? sleepBlocker,
        IDestinationInfo? destinationInfo = null,
        TimeProvider? time = null)
        : this(fs, scheduler, resolver, jobsDir, sleepBlocker, destinationInfo, time, WindowsElevationCheck.Instance)
    {
    }

    /// <summary>For tests only: the elevation check is not public, so a host cannot switch the refusal off.</summary>
    internal TreeCopier(
        IFileSystem fs,
        DriveAwareScheduler scheduler,
        IConflictResolver resolver,
        string jobsDir,
        ISleepBlocker? sleepBlocker,
        IDestinationInfo? destinationInfo,
        TimeProvider? time,
        IElevationCheck elevation)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);
        FileSystem = fs;
        Scheduler = scheduler;
        Resolver = resolver;
        JobsDir = jobsDir;
        SleepBlocker = sleepBlocker ?? WindowsSleepBlocker.Instance;
        DestinationInfo = destinationInfo ?? WindowsDestinationInfo.Instance;
        Time = time ?? TimeProvider.System;
        Copier = new FileCopier(fs, BufferPool.Shared, Time);
        ArgumentNullException.ThrowIfNull(elevation);
        Elevation = elevation;
    }

    /// <summary>
    /// Copies or moves every item of <paramref name="sources"/> (files or folders, full paths) into the folder <paramref name="destination"/>
    /// (created if missing): a folder <c>X\photos</c> lands at <c>destination\photos</c>; a drive root's contents land directly in it.
    /// Cancelling throws <see cref="OperationCanceledException"/> and leaves the job resumable.
    /// </summary>
    public Task<CopyResult> RunAsync(
        Guid jobId, IReadOnlyList<string> sources, string destination, CopyMode mode, CopyOptions options, PauseGate pause, IProgress<JobProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(progress);
        var spec = new JobSpec(JobSpec.CurrentVersion, [.. sources], destination, mode, options);
        if (!spec.IsValid())
        {
            throw new ArgumentException("Sources and destination must be full paths, with at least one source, and the options must be valid.", nameof(sources));
        }

        if (Elevation.IsElevated)
        {
            // An elevated copier walking user-controlled paths is a privilege-escalation risk (planted links): refuse, never advise elevating.
            return Task.FromResult(JobOutcome.Refused(destination, CopyMessages.RunningElevated));
        }

        return new TreeJob(this, jobId, spec, null, pause, progress, ct).RunAsync();
    }

    /// <summary>
    /// Continues a job from its journal: done files are skipped and part files the journal names are removed first. The saved job is
    /// untrusted (anyone who can write the jobs folder can edit it), so <paramref name="confirmation"/> is shown its sources, destination and
    /// mode first, and nothing runs unless it says yes.
    /// </summary>
    public async Task<CopyResult> ResumeAsync(Guid jobId, IResumeConfirmation confirmation, PauseGate pause, IProgress<JobProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(progress);
        if (Elevation.IsElevated)
        {
            return JobOutcome.Refused(string.Empty, CopyMessages.RunningElevated);
        }

        var journal = JobJournal.Open(JobsDir, jobId, Time);
        var handedOver = false;
        try
        {
            if (journal.IsFinished)
            {
                return new CopyResult(0, 0, 0, [], [], AllVerified: false); // nothing was verified in this run
            }

            var spec = JobSpec.Parse(journal.OptionsJson);
            if (!await confirmation.ConfirmAsync(new ResumeRequest(spec.Sources, spec.Destination, spec.Mode), ct).ConfigureAwait(false))
            {
                return JobOutcome.Refused(spec.Destination, CopyMessages.ResumeNotConfirmed);
            }

            handedOver = true; // the job disposes the journal from here on
            return await new TreeJob(this, jobId, spec, journal, pause, progress, ct).RunAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!handedOver)
            {
                journal.Dispose();
            }
        }
    }
}
