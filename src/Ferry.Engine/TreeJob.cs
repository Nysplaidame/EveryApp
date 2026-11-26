using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Scheduling;

namespace Ferry.Engine;

/// <summary>
/// One run of one job: drives the phases (checking, waiting, copying) with the components <see cref="TreePlanner"/>, <see cref="TreeMover"/>,
/// <see cref="ResumeRecovery"/>, <see cref="ChecksumWriter"/> and <see cref="ProgressTracker"/>. Every decision about a file is made here one
/// file at a time in planned order; only the copy itself may run alongside others (<see cref="InFlightCopies"/>). See <see cref="TreeCopier"/>
/// for the rules it keeps.
/// </summary>
internal sealed partial class TreeJob
{
    /// <summary>Free space the journal's drive must have before a job starts.</summary>
    internal const long JournalSpaceFloor = 50L * 1024 * 1024;

    private readonly TreeCopier _owner;
    private readonly Guid _jobId;
    private readonly JobSpec _spec;
    private readonly PauseGate _pause;
    private readonly CancellationToken _ct;
    private readonly IFileSystem _fs;
    private readonly CopyOptions _options;
    private readonly bool _isMove;
    private readonly string _destination;
    private readonly JobOutcome _outcome;
    private readonly JobJournalWriter _journal;
    private readonly ProgressTracker _progress;
    private readonly ResumeRecovery _recovery;
    private readonly TreeMover _mover;
    private readonly ChecksumWriter _checksums;
    private readonly List<(FileEntry Source, string Target, bool LocalDrives)> _createdFolders = [];
    private readonly List<(FileEntry Entry, string Root)> _sourceFolders = [];
    private string? _destinationFinal;
    private bool _keepsStreams;
    private bool _clone;
    private bool _sameVolume;
    private bool _localDrives;
    private readonly SpeedLimiter? _limiter;
    private LargeFileGates? _gates; // one per job, shared by every copy; disposed only once every copy has ended
    private int _workers = 1;

    public TreeJob(TreeCopier owner, Guid jobId, JobSpec spec, JobJournal? journal, PauseGate pause, IProgress<JobProgress> sink, CancellationToken ct)
    {
        _owner = owner;
        _jobId = jobId;
        _spec = spec;
        _pause = pause;
        _ct = ct;
        _fs = owner.FileSystem;
        _options = spec.Options;
        _isMove = spec.Mode == CopyMode.Move;
        _destination = spec.Destination;
        _outcome = new JobOutcome(VerifyOn);
        _journal = new JobJournalWriter(journal);
        _progress = new ProgressTracker(sink, owner.Time);
        _recovery = new ResumeRecovery(_fs, _destination, journal, p => _outcome.Problems.Add(p));
        _mover = new TreeMover(_fs, _destination, _outcome, _journal, _progress, Fail);
        _checksums = new ChecksumWriter(_fs, _destination, _options.HashAlgorithm, _journal, _outcome);
        _limiter = _options.SpeedLimitBytesPerSecond is { } rate ? new SpeedLimiter(rate, owner.Time) : null; // one per job, shared by every copy in it
    }

    private bool VerifyOn => _options.Verify || _isMove;

    private CopyOptions CopierOptions => VerifyOn == _options.Verify ? _options : _options with { Verify = true };

    public async Task<CopyResult> RunAsync()
    {
        // Every root waits for all of its copies before it ends (even when cancelled or failing), so nothing uses these once this returns.
        using var gates = new LargeFileGates();
        _gates = gates;
        try
        {
            return await RunCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _journal.Journal?.Dispose();
        }
    }

    private async Task<CopyResult> RunCoreAsync()
    {
        _progress.Report();

        // ---- Checking: overlap, walk, pre-flight. Nothing is written in this phase.
        var planner = new TreePlanner(_fs, _spec, _recovery.Completed, _outcome);
        var destinationVolume = _fs.GetVolumeId(_destination);
        planner.CheckOverlap();
        if (_outcome.Issues.Count > 0)
        {
            return _outcome.ToResult(stopped: true, refused: true);
        }

        var roots = planner.Plan(destinationVolume);
        planner.RunPreflight(roots, _progress, _owner.DestinationInfo);
        if (_outcome.Issues.Any(TreePlanner.IsBlocking))
        {
            return _outcome.ToResult(stopped: true, refused: true);
        }

        var fileSystem = _owner.DestinationInfo.FileSystemOf(_destination);
        if (_options.DryRun)
        {
            return planner.DryRun(roots);
        }

        // ---- The job's record needs room: refuse up front rather than fail half way with a journal that cannot grow.
        if (_owner.DestinationInfo.FreeBytes(_owner.JobsDir) is { } free && free < JournalSpaceFloor)
        {
            var drive = (Path.GetPathRoot(Path.GetFullPath(_owner.JobsDir)) ?? _owner.JobsDir).TrimEnd('\\');
            _outcome.Problems.Add(new Problem(drive, IoErrorKind.DiskFull, CopyMessages.JournalNoSpace(drive)));
            return _outcome.ToResult(stopped: true, refused: true);
        }

        // ---- Wait for every drive involved (all sources and the destination), keep awake, journal: all before the first write.
        var devices = _owner.Scheduler.DevicesOf([.. _spec.Sources, _destination]);
        _workers = ChooseWorkers(devices);
        var label = _spec.Sources.Count == 1 ? $"{_spec.Sources[0]} to {_destination}" : $"{_spec.Sources.Count} items to {_destination}";
        var acquire = _owner.Scheduler.AcquireAsync(devices, label, _ct);
        if (!acquire.IsCompleted)
        {
            _progress.SetPhase("Waiting");
        }

        var lease = await acquire.ConfigureAwait(false);
        try
        {
            using var awake = _owner.SleepBlocker.Acquire();
            _journal.Journal ??= JobJournal.Create(_owner.JobsDir, _jobId, _spec.ToJson(), _owner.Time);
            try
            {
                _fs.CreateDirectory(_destination);
                _destinationFinal = _fs.GetFinalPath(_destination); // every part file must end up under this
            }
            catch (FileSystemException ex)
            {
                _outcome.Problems.Add(new Problem(TreePaths.Display(_destination), ex.Kind, WriteMessage(ex.Kind)));
                return _outcome.ToResult(stopped: true, refused: true);
            }

            _keepsStreams = !(fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                || fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase));
            _recovery.DeleteJournaledParts();
            _progress.SetPhase("Copying");

            foreach (var root in roots)
            {
                if (_outcome.Stopped)
                {
                    break;
                }

                var rootOnDestinationVolume = destinationVolume is not null
                    && string.Equals(_fs.GetVolumeId(root.Path), destinationVolume, StringComparison.OrdinalIgnoreCase);
                _clone = !_isMove && rootOnDestinationVolume && fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
                _sameVolume = root.SameVolume;
                _localDrives = _owner.DestinationInfo.IsFixedLocalDrive(root.Path) && _owner.DestinationInfo.IsFixedLocalDrive(_destination);
                await ProcessRootAsync(root).ConfigureAwait(false);
            }

            FinishFolders();
            if (_options.ExportChecksums)
            {
                _checksums.Write(_destinationFinal, _ct);
            }

            if (!_outcome.Stopped)
            {
                _journal.Append(new JournalRecord(JournalRecordKind.JobFinished, null, 0, DateTime.UtcNow, null,
                    $"copied {_outcome.Copied}, skipped {_outcome.Skipped}, failed {_outcome.Failed}"));
            }

            _progress.Report();
            return _outcome.ToResult(_outcome.Stopped);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Several copies at once only when every drive (each source and the destination) is a fixed local solid-state drive and the job is not
    /// rescuing a failing drive; anything unknown means one at a time. The drive kind is asked last, so a network path is never queried.
    /// </summary>
    private int ChooseWorkers(DeviceSet devices) =>
        _owner.Workers > 1 && !_options.RescueMode
        && _owner.DestinationInfo.IsFixedLocalDrive(_destination) && _spec.Sources.All(_owner.DestinationInfo.IsFixedLocalDrive)
        && _owner.DriveKinds.KindOf(devices) == DriveKind.SolidState
            ? _owner.Workers
            : 1;

    private async Task ProcessRootAsync(TreeRoot root)
    {
        if (root.MoveByRename)
        {
            var files = root.Items.Where(i => i.Kind == TreeItemKind.File).ToList();
            if (_mover.MoveWhole(root.Entry.FullPath, root.Key, root.JournalKey, files.Count, files.Sum(i => i.Entry.Length)) != MoveOutcome.NotSameDevice)
            {
                return;
            }

            _sameVolume = false; // Windows says another volume after all: copy and verify item by item
        }

        // A same-volume move is renames: nothing to gain from several at once, and its fall-back to copying must stay in planned order.
        using var inFlight = new InFlightCopies(root.SameVolume ? 1 : _workers);
        try
        {
            await DispatchAsync(root, inFlight).ConfigureAwait(false);
        }
        finally
        {
            await inFlight.WaitForAllAsync().ConfigureAwait(false); // the root (and the job) never ends with a copy still running
        }

        inFlight.ThrowIfFailed();
    }

    /// <summary>Walks the root's items in planned order, deciding each one here and handing only file copies to <paramref name="inFlight"/>.</summary>
    private async Task DispatchAsync(TreeRoot root, InFlightCopies inFlight)
    {
        // Counted once per root (not once per folder), and skipped folders kept in a set: no quadratic work on huge trees.
        var counts = CountFiles(root);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clone = _clone;
        var localDrives = _localDrives;
        foreach (var item in root.Items)
        {
            if (_outcome.Stopped || StopForFailure(inFlight))
            {
                return;
            }

            _ct.ThrowIfCancellationRequested();
            if (IsInside(item.Key, skipped))
            {
                continue;
            }

            switch (item.Kind)
            {
                case TreeItemKind.Folder:
                    // A copy still running may hold this very name (a renamed file): decide once it has landed, as one at a time would.
                    await inFlight.EndOf(Path.Combine(_destination, item.Key)).WaitAsync(_ct).ConfigureAwait(false);
                    if (!ProcessFolder(root, item, counts))
                    {
                        skipped.Add(item.Key);
                    }

                    break;
                case TreeItemKind.Link:
                    _mover.MoveLink(_sameVolume, item);
                    break;
                default:
                    await ProcessFileAsync(root, item, inFlight, clone, localDrives).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// True when the file system may resolve this file's name to a different file's name: it looks like a short 8.3 name (a tilde and digits at
    /// the end of the name or just before the extension), or ends with a dot or a space.
    /// </summary>
    private static bool MayResolveToAnotherName(string key)
    {
        var name = Path.GetFileName(key);
        return name.Length > 0 && (name[^1] is '.' or ' ' || ShortNameLike().IsMatch(name));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"~[0-9]+(\.[^.]*)?$")]
    private static partial System.Text.RegularExpressions.Regex ShortNameLike();

    /// <summary>True when a copy ended with an unexpected error: the job starts nothing new, waits for the others, then rethrows it.</summary>
    private bool StopForFailure(InFlightCopies inFlight)
    {
        if (!inFlight.Failed)
        {
            return false;
        }

        _outcome.MarkStopped();
        return true;
    }

    /// <summary>Files and bytes under every folder key of the root (each file adds to each of its ancestors).</summary>
    private static Dictionary<string, (int Files, long Bytes)> CountFiles(TreeRoot root)
    {
        var counts = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.Items)
        {
            if (item.Kind != TreeItemKind.File)
            {
                continue;
            }

            for (var folder = Path.GetDirectoryName(item.Key); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                var (files, bytes) = counts.GetValueOrDefault(folder);
                counts[folder] = (files + 1, bytes + item.Entry.Length);
            }
        }

        return counts;
    }

    private static bool IsInside(string key, HashSet<string> folders)
    {
        if (folders.Count == 0)
        {
            return false;
        }

        for (var folder = Path.GetDirectoryName(key); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (folders.Contains(folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns false when the folder's contents must be skipped (they moved with it, it is a link, or it could not be created).</summary>
    private bool ProcessFolder(TreeRoot root, TreeItem item, Dictionary<string, (int Files, long Bytes)> counts)
    {
        var target = Path.Combine(_destination, item.Key);
        var existing = TreePaths.TryGet(_fs, target, follow: false);
        var (files, bytes) = counts.GetValueOrDefault(item.Key);
        if (existing is not null && TreePaths.IsNameSurrogate(existing))
        {
            // Never write through a link that is already in the destination: it could lead anywhere.
            _outcome.AddFailed(files);
            _outcome.Problems.Add(new Problem(TreePaths.Display(target), IoErrorKind.None, CopyMessages.DestinationFolderIsLink));
            _progress.CompleteFiles(files, bytes);
            return false;
        }

        if (existing is not null && !existing.Attributes.HasFlag(FileAttributes.Directory))
        {
            _outcome.AddFailed(files);
            _outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.FileInTheWay));
            _progress.CompleteFiles(files, bytes);
            return false;
        }

        if (existing is null && _sameVolume && _options.Exclude.Count == 0 && item.Key != root.Key)
        {
            if (_mover.MoveWhole(item.Entry.FullPath, item.Key, item.JournalKey, files, bytes) != MoveOutcome.NotSameDevice)
            {
                return false;
            }

            _sameVolume = false; // copy and verify from here on
        }

        if (_isMove)
        {
            _sourceFolders.Add((item.Entry, root.Path)); // removed deepest first at the end, only if empty and still the same folder
        }

        if (existing is not null)
        {
            return true;
        }

        try
        {
            _fs.CreateDirectory(target);
            _createdFolders.Add((item.Entry, target, _localDrives));
            return true;
        }
        catch (FileSystemException ex)
        {
            _outcome.AddFailed(files);
            _outcome.Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, WriteMessage(ex.Kind)));
            _progress.CompleteFiles(files, bytes);
            return false;
        }
    }

    /// <summary>
    /// Decides one file (resume checks, existing target, conflict rule or question, free name) right here, in planned order, then hands its
    /// copy to <paramref name="inFlight"/>. Waits for a free place first, so no question is asked and no name chosen ahead of a free copy.
    /// </summary>
    private async Task ProcessFileAsync(TreeRoot root, TreeItem item, InFlightCopies inFlight, bool clone, bool localDrives)
    {
        var entry = item.Entry;
        if (_recovery.Completed.Contains(item.JournalKey))
        {
            return;
        }

        await _pause.WaitIfPausedAsync(_ct).ConfigureAwait(false);
        await inFlight.WaitForPlaceAsync(_ct).ConfigureAwait(false);
        ProgressTracker.FileSlot? slot = null;
        var handedOver = false;
        try
        {
            if (_outcome.Stopped || inFlight.Failed)
            {
                return; // a copy that was running stopped the job while this file waited for its turn
            }

            var started = _progress.StartFile(item.Key);
            slot = started;
            Task CopyAsync(string target, string targetKey, bool replace)
            {
                // A running copy may have stopped the job while this file was being decided (a question, a wait for another copy, the
                // resume checks): then it is not started at all, just as a one-at-a-time job would never have reached it.
                if (_outcome.Stopped || inFlight.Failed)
                {
                    return Task.CompletedTask;
                }

                handedOver = true; // from here the copy ends the file's progress and gives its place back, whatever happens
                return inFlight.StartAsync(target, () => CopyAndEndAsync(root, item, target, targetKey, replace, started, clone, localDrives));
            }

            if (MayResolveToAnotherName(item.Key))
            {
                // The file system may treat this name as the same file as another one (a short 8.3 name, or a trailing dot or space): so the
                // existence check and the free-name search must not run while a copy into this folder is still landing.
                await inFlight.EndOfAllIn(Path.GetDirectoryName(Path.Combine(_destination, item.Key)) ?? _destination).WaitAsync(_ct).ConfigureAwait(false);
            }

            if (_recovery.IsResume)
            {
                // The resume checks read files in the destination folder: never while another copy is still committing one there.
                await inFlight.EndOfAllIn(Path.GetDirectoryName(Path.Combine(_destination, item.Key)) ?? _destination).WaitAsync(_ct).ConfigureAwait(false);
            }

            var notOnDisk = _recovery.IsNotOnDisk(item.JournalKey);
            if (!notOnDisk && !_sameVolume && await TryRecoverAsync(item, root).ConfigureAwait(false))
            {
                return;
            }

            // Resuming a copy that may have skipped forcing small files to disk: a power cut can leave this job's own file blank under its real
            // name, even with the journal's last records lost. Still the source's exact data: done (counted already if its done record survived).
            // Damaged: replace it without a conflict question. Anything else is not this job's file and goes through the conflict rule below.
            if (_recovery.IsResume && !_isMove && !_sameVolume)
            {
                var recheck = await _recovery.RecheckAsync(item, _options.HashAlgorithm, _ct).ConfigureAwait(false);
                if (recheck.Result == RecheckResult.Intact && notOnDisk)
                {
                    return;
                }

                if (recheck.Result == RecheckResult.Damaged)
                {
                    await CopyAsync(Path.Combine(_destination, recheck.TargetKey), recheck.TargetKey, replace: true).ConfigureAwait(false);
                    return;
                }
            }

            var target = Path.Combine(_destination, item.Key);
            var targetKey = item.Key;

            // Another file's copy may be heading for this name (it was renamed to it): once it lands, decide as one at a time would.
            await inFlight.EndOf(target).WaitAsync(_ct).ConfigureAwait(false);
            var existing = TreePaths.TryGet(_fs, target, follow: false);
            var replace = false;
            if (existing is not null)
            {
                if (existing.Attributes.HasFlag(FileAttributes.Directory))
                {
                    Fail(item, new Problem(TreePaths.Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.FolderInTheWay));
                    return;
                }

                switch (await ConflictPolicy.DecideAsync(_options.IfExists, _owner.Resolver, entry, existing, _ct).ConfigureAwait(false))
                {
                    case ConflictRule.Overwrite:
                        replace = true;
                        break;
                    case ConflictRule.Rename: // also KeepBoth (R30): the incoming file gets the new name, the existing file is never touched
                        var renamed = NameAllocator.NextFreeName(_fs, target, inFlight.IsReserved); // a name held by a running copy is taken
                        if (renamed is null)
                        {
                            Fail(item, new Problem(TreePaths.Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.NoFreeName));
                            return;
                        }

                        target = renamed;
                        targetKey = TreePaths.Combine(Path.GetDirectoryName(item.Key) ?? string.Empty, Path.GetFileName(renamed));
                        break;
                    default:
                        _outcome.AddSkipped();
                        _journal.Append(new JournalRecord(JournalRecordKind.FileSkipped, item.JournalKey, entry.Length, entry.LastWriteTimeUtc, null, null));
                        return;
                }
            }

            if (_sameVolume)
            {
                if (_mover.MoveFile(item, target, replace) != MoveOutcome.NotSameDevice)
                {
                    return;
                }

                _sameVolume = false; // the rename was refused as crossing volumes: copy and verify instead (never an unverified copy)
            }

            await CopyAsync(target, targetKey, replace).ConfigureAwait(false);
        }
        finally
        {
            if (!handedOver)
            {
                slot?.End(entry.Length); // skipped, failed, moved by rename or recovered: done here
                inFlight.GiveBack();
            }
        }
    }

    /// <summary>The part of a file that may run alongside others: its copy, then the end of its progress whatever happened.</summary>
    private async Task CopyAndEndAsync(
        TreeRoot root, TreeItem item, string target, string targetKey, bool replace, ProgressTracker.FileSlot slot, bool clone, bool localDrives)
    {
        try
        {
            await CopyFileAsync(root, item, target, targetKey, replace, slot, clone, localDrives).ConfigureAwait(false);
        }
        finally
        {
            slot.End(item.Entry.Length);
        }
    }

    /// <summary>
    /// Copies one file with retries; on success applies permissions, deletes the source of a move, and journals it done. Everything it needs
    /// that differs per root comes in as arguments, never from fields the dispatcher changes while copies run.
    /// </summary>
    private async Task CopyFileAsync(
        TreeRoot root, TreeItem item, string target, string targetKey, bool replace, ProgressTracker.FileSlot slot, bool clone, bool localDrives)
    {
        var entry = item.Entry;
        var source = entry.FullPath;
        var options = CopierOptions;
        var targetFolderKey = Path.GetDirectoryName(targetKey) ?? string.Empty;

        // For a move: the source as it is right now (from its handle); it is deleted later only if it is still exactly this.
        FileEntry? expected = null;
        if (_isMove)
        {
            try
            {
                expected = _fs.GetEntry(source);
            }
            catch (FileSystemException ex)
            {
                Fail(item, new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: false, CopyMessages.DriveOf(_destination))));
                return;
            }
        }

        FileCopyOutcome outcome;
        var verifyFailed = false;
        var warnings = new List<Problem>();
        for (var attempt = 0; ; attempt++)
        {
            verifyFailed = false;
            warnings.Clear();
            var hooks = new FileCopyHooks
            {
                // R29: the part name (and the real name it is for) must be in the journal before the part exists; if it cannot be, no part is made.
                PartChosen = part => _journal.TryAppend(new JournalRecord(
                    JournalRecordKind.FilePartStarted, item.JournalKey, entry.Length, entry.LastWriteTimeUtc, null,
                    TreePaths.Combine(targetFolderKey, Path.GetFileName(part)), targetKey)),
                SharedLimiter = _limiter,
                LargeGates = _gates,
                ReplaceExisting = replace,
                TryBlockClone = clone,

                // Forcing each small file to disk is most of its copy time. The journal marks such files and a resume checks them again;
                // a move never skips it, because the source is deleted right after.
                SkipSmallFileFlush = !_isMove,
                MustBeUnder = _destinationFinal,
                Status = _progress.StatusSink(),

                // The copier reads the copy back before the real name appears (small files before their handle closes, see VerifyInCopier).
                VerifyInCopier = true,
                Verifying = slot.Verifying,
                BeforeCommit = (part, hash, token) =>
                {
                    // The "downloaded from the internet" marker always travels with the file; other streams only when asked.
                    if (_keepsStreams)
                    {
                        try
                        {
                            _fs.CopyAlternateStreams(source, part, allStreams: _options.CopyAlternateStreams, token);
                        }
                        catch (FileSystemException ex)
                        {
                            if (_isMove)
                            {
                                return ValueTask.FromResult<Problem?>(new Problem(source, ex.Kind, CopyMessages.StreamsNotCopiedOriginalKept)); // never lose the marker by moving
                            }

                            warnings.Add(new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.StreamsNotCopied));
                        }
                    }

                    return ValueTask.FromResult<Problem?>(null);
                },
            };

            outcome = await _owner.Copier.CopyAsync(source, target, options, _pause, slot.Bytes, hooks, _ct).ConfigureAwait(false);
            verifyFailed = outcome.VerifyFailed;
            if (outcome.Success || outcome.NoRetry || attempt >= TreeCopier.RetryDelays.Length || !IsWorthRetrying(outcome.Problem!.Kind))
            {
                break;
            }

            slot.Reset();
            await Task.Delay(TreeCopier.RetryDelays[attempt], _owner.Time, _ct).ConfigureAwait(false);
        }

        if (!outcome.Success)
        {
            if (verifyFailed)
            {
                _outcome.MarkVerifyFailed();
            }

            var p = outcome.Problem!;
            Fail(item, p with { Path = TreePaths.Display(p.Path) });
            return;
        }

        if (options.Verify && outcome.Lost.Count == 0)
        {
            _outcome.AddVerified(); // BeforeCommit only lets a matching copy through
        }

        _outcome.Problems.AddRange(warnings);
        if (outcome.Problem is { } warning)
        {
            _outcome.Problems.Add(warning with { Path = TreePaths.Display(warning.Path) });
        }

        CopyPermissions(source, target, localDrives);
        if (expected is not null && outcome.Lost.Count == 0)
        {
            DeleteSource(source, expected, root.Path); // only now: the copy is complete and matched
        }
        else if (expected is not null)
        {
            _outcome.Problems.Add(new Problem(TreePaths.Display(source), IoErrorKind.BadBlock, CopyMessages.RescuedOriginalKept));
        }

        var hex = outcome.SourceHash is null ? null : Convert.ToHexStringLower(outcome.SourceHash);
        if (_options.ExportChecksums && hex is not null)
        {
            _checksums.Add(hex, targetKey);
        }

        _outcome.AddCopied();
        // A rescued copy must not be recovered later as a verified move that can delete its source.
        if (outcome.Lost.Count == 0)
        {
            _journal.Done(item.JournalKey, entry.Length, entry.LastWriteTimeUtc, hex, onDisk: outcome.Flushed);
        }
    }

    private static bool IsWorthRetrying(IoErrorKind kind) =>
        kind is IoErrorKind.BadBlock or IoErrorKind.NetworkLost or IoErrorKind.SharingViolation or IoErrorKind.Other;

    /// <summary>Deletes a moved file's source through a handle, only if it is still the very file that was copied and verified.</summary>
    private void DeleteSource(string source, FileEntry expected, string sourceRoot)
    {
        try
        {
            if (!_fs.DeleteVerified(source, expected.FileId, expected.VolumeSerial, expected.Length, expected.LastWriteTimeUtc, sourceRoot))
            {
                _outcome.Problems.Add(new Problem(TreePaths.Display(source), IoErrorKind.None, CopyMessages.OriginalChanged));
            }
        }
        catch (FileSystemException ex)
        {
            _outcome.Problems.Add(new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.OriginalNotRemoved));
        }
    }

    /// <summary>
    /// A file renamed into place just before a crash (its recorded target holds exactly the source's data): re-apply metadata and
    /// permissions, finish a move, journal it done.
    /// </summary>
    private async Task<bool> TryRecoverAsync(TreeItem item, TreeRoot root)
    {
        FileEntry before;
        try
        {
            before = _fs.GetEntry(item.Entry.FullPath);
        }
        catch (FileSystemException)
        {
            return false;
        }

        if (await _recovery.FindCommittedAsync(item, _options.HashAlgorithm, _ct).ConfigureAwait(false) is not { } found)
        {
            return false;
        }

        var target = Path.Combine(_destination, found.TargetKey);
        try
        {
            if (_options.KeepDatesAndAttributes)
            {
                var e = _fs.GetEntryFollowingLinks(item.Entry.FullPath);
                var attributes = e.Attributes & FileCopier.CopiedAttributes;
                _fs.SetMetadata(target, attributes == 0 ? FileAttributes.Normal : attributes, e.CreationTimeUtc, e.LastWriteTimeUtc);
            }
        }
        catch (FileSystemException)
        {
            return false;
        }

        CopyPermissions(item.Entry.FullPath, target);
        if (_isMove)
        {
            DeleteSource(item.Entry.FullPath, before, root.Path); // the content was just checked against the recorded target
        }

        var hex = Convert.ToHexStringLower(found.Hash);
        if (_options.ExportChecksums)
        {
            _checksums.Add(hex, found.TargetKey);
        }

        _outcome.AddCopied();
        _outcome.AddVerified();
        _journal.Done(item.JournalKey, item.Entry.Length, item.Entry.LastWriteTimeUtc, hex);
        return true;
    }

    /// <summary>Copies permissions when asked, only between this PC's fixed drives (elsewhere they could widen access or mean nothing).</summary>
    private void CopyPermissions(string source, string target, bool? localDrives = null)
    {
        if (!_options.CopyPermissions)
        {
            return;
        }

        if (!(localDrives ?? _localDrives))
        {
            _outcome.PermissionsNotLocal(target);
            return;
        }

        try
        {
            _outcome.PermissionOutcome(target, _fs.CopySecurity(source, target));
        }
        catch (FileSystemException ex)
        {
            _outcome.PermissionProblem(target, ex);
        }
    }

    private void Fail(TreeItem item, Problem problem)
    {
        _outcome.AddFailed();
        _outcome.Problems.Add(problem);
        _journal.Append(new JournalRecord(JournalRecordKind.FileFailed, item.JournalKey, item.Entry.Length, item.Entry.LastWriteTimeUtc, null, problem.Message));
        switch (_options.IfUnreadable)
        {
            case FailureRule.Stop:
                _outcome.MarkStopped();
                _outcome.Problems.Add(new Problem(problem.Path, IoErrorKind.None, CopyMessages.StoppedAfterProblem));
                break;
            case FailureRule.Ask:
                // No question interface yet (M2 UI): Ask behaves like RetryThenSkipAndList, so the file is listed and the job goes on.
                break;
            default:
                break; // RetryThenSkipAndList: retried already, now listed; the job goes on
        }
    }

    /// <summary>Dates, attributes and permissions of folders this job created (after their files, which change folder dates), and moved-out source folders.</summary>
    private void FinishFolders()
    {
        for (var i = _createdFolders.Count - 1; i >= 0; i--)
        {
            var (source, target, localDrives) = _createdFolders[i];
            if (_options.KeepDatesAndAttributes)
            {
                var attributes = (source.Attributes & FileCopier.CopiedAttributes & ~FileAttributes.ReadOnly) | FileAttributes.Directory;
                try
                {
                    _fs.SetMetadata(target, attributes, source.CreationTimeUtc, source.LastWriteTimeUtc);
                }
                catch (FileSystemException)
                {
                    // Folder dates are a nicety; the files are what matter.
                }
            }

            CopyPermissions(source.FullPath, target, localDrives);
        }

        // Deepest first. Only an empty folder that is still the one walked goes (checked and deleted through one handle); anything
        // excluded, failed or left as a link keeps its folder.
        for (var i = _sourceFolders.Count - 1; i >= 0; i--)
        {
            var (entry, root) = _sourceFolders[i];
            try
            {
                _fs.DeleteEmptyFolderVerified(entry.FullPath, entry.FileId, entry.VolumeSerial, root);
            }
            catch (FileSystemException)
            {
                // Already gone or not ours to remove: nothing to do.
            }
        }
    }

    private string WriteMessage(IoErrorKind kind) => CopyMessages.For(kind, whileWriting: true, CopyMessages.DriveOf(_destination));
}
