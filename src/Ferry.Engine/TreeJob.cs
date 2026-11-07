using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>
/// One run of one job: drives the phases (checking, waiting, copying) and copies files one at a time with the components
/// <see cref="TreePlanner"/>, <see cref="TreeMover"/>, <see cref="ResumeRecovery"/>, <see cref="ChecksumWriter"/> and
/// <see cref="ProgressTracker"/>. See <see cref="TreeCopier"/> for the rules it keeps.
/// </summary>
internal sealed class TreeJob
{
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
    private readonly List<(FileEntry Source, string Target)> _createdFolders = [];
    private readonly List<string> _sourceFolders = [];
    private bool _clone;

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
        _recovery = new ResumeRecovery(_fs, _destination, journal);
        _mover = new TreeMover(_fs, _destination, _outcome, _journal, _progress, Fail);
        _checksums = new ChecksumWriter(_fs, _destination, _options.HashAlgorithm, _journal, _outcome);
    }

    private bool VerifyOn => _options.Verify || _isMove;

    private CopyOptions CopierOptions => VerifyOn == _options.Verify ? _options : _options with { Verify = true };

    public async Task<CopyResult> RunAsync()
    {
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
        var destinationEntry = TreePaths.NearestExisting(_fs, _destination);
        planner.CheckOverlap();
        if (_outcome.Issues.Count > 0)
        {
            return _outcome.ToResult(stopped: true, refused: true);
        }

        var roots = planner.Plan(destinationEntry);
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

        // ---- Wait for every drive involved (all sources and the destination), keep awake, journal: all before the first write.
        var devices = _owner.Scheduler.DevicesOf([.. _spec.Sources, _destination]);
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
            }
            catch (FileSystemException ex)
            {
                _outcome.Problems.Add(new Problem(TreePaths.Display(_destination), ex.Kind, WriteMessage(ex.Kind)));
                return _outcome.ToResult(stopped: true, refused: true);
            }

            _recovery.DeleteJournaledParts();
            _progress.SetPhase("Copying");

            foreach (var root in roots)
            {
                if (_outcome.Stopped)
                {
                    break;
                }

                _clone = !_isMove && fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
                    && destinationEntry is not null && root.Entry.VolumeSerial == destinationEntry.VolumeSerial;
                await ProcessRootAsync(root).ConfigureAwait(false);
            }

            FinishFolders();
            if (_options.ExportChecksums)
            {
                _checksums.Write(_ct);
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

    private async Task ProcessRootAsync(TreeRoot root)
    {
        if (root.MoveByRename)
        {
            var files = root.Items.Where(i => i.Kind == TreeItemKind.File).ToList();
            _mover.MoveWhole(root.Entry.FullPath, root.Key, root.JournalKey, files.Count, files.Sum(i => i.Entry.Length));
            return;
        }

        var skipUnder = new List<string>();
        foreach (var item in root.Items)
        {
            if (_outcome.Stopped)
            {
                return;
            }

            _ct.ThrowIfCancellationRequested();
            if (skipUnder.Exists(prefix => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            switch (item.Kind)
            {
                case TreeItemKind.Folder:
                    if (!ProcessFolder(root, item))
                    {
                        skipUnder.Add(item.Key + "\\");
                    }

                    break;
                case TreeItemKind.Link:
                    _mover.MoveLink(root, item);
                    break;
                default:
                    await ProcessFileAsync(root, item).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>Returns false when the folder's contents must be skipped (they moved with it, or it could not be created).</summary>
    private bool ProcessFolder(TreeRoot root, TreeItem item)
    {
        var target = Path.Combine(_destination, item.Key);
        var existing = TreePaths.TryGet(_fs, target, follow: false);
        var (files, bytes) = CountUnder(root, item.Key);
        if (existing is not null && !existing.Attributes.HasFlag(FileAttributes.Directory))
        {
            _outcome.Failed += files;
            _outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.FileInTheWay));
            _progress.CompleteFiles(files, bytes);
            return false;
        }

        if (existing is null && root.SameVolume && _options.Exclude.Count == 0 && item.Key != root.Key)
        {
            _mover.MoveWhole(item.Entry.FullPath, item.Key, item.JournalKey, files, bytes);
            return false;
        }

        if (_isMove)
        {
            _sourceFolders.Insert(0, item.Entry.FullPath); // deepest first when removing emptied folders
        }

        if (existing is not null)
        {
            return true;
        }

        try
        {
            _fs.CreateDirectory(target);
            _createdFolders.Add((item.Entry, target));
            return true;
        }
        catch (FileSystemException ex)
        {
            _outcome.Failed += files;
            _outcome.Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, WriteMessage(ex.Kind)));
            _progress.CompleteFiles(files, bytes);
            return false;
        }
    }

    private static (int Files, long Bytes) CountUnder(TreeRoot root, string folderKey)
    {
        var prefix = folderKey + "\\";
        var files = root.Items.Where(i => i.Kind == TreeItemKind.File && i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return (files.Count, files.Sum(i => i.Entry.Length));
    }

    private async Task ProcessFileAsync(TreeRoot root, TreeItem item)
    {
        var entry = item.Entry;
        if (_recovery.Completed.Contains(item.JournalKey))
        {
            return;
        }

        await _pause.WaitIfPausedAsync(_ct).ConfigureAwait(false);
        _progress.StartFile(item.Key);
        try
        {
            if (_isMove && !root.SameVolume && TreePaths.IsNameSurrogate(entry))
            {
                _outcome.Problems.Add(new Problem(TreePaths.Display(entry.FullPath), IoErrorKind.None, CopyMessages.FileLinkNotMovedAcrossDrives));
                return;
            }

            if (!root.SameVolume && await TryRecoverAsync(item).ConfigureAwait(false))
            {
                return;
            }

            var target = Path.Combine(_destination, item.Key);
            var targetKey = item.Key;
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
                        var renamed = NameAllocator.NextFreeName(_fs, target);
                        if (renamed is null)
                        {
                            Fail(item, new Problem(TreePaths.Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.NoFreeName));
                            return;
                        }

                        target = renamed;
                        targetKey = TreePaths.Combine(Path.GetDirectoryName(item.Key) ?? string.Empty, Path.GetFileName(renamed));
                        break;
                    default:
                        _outcome.Skipped++;
                        _journal.Append(new JournalRecord(JournalRecordKind.FileSkipped, item.JournalKey, entry.Length, entry.LastWriteTimeUtc, null, null));
                        return;
                }
            }

            if (root.SameVolume)
            {
                _mover.MoveFile(item, target, replace);
            }
            else
            {
                await CopyFileAsync(item, target, targetKey, replace).ConfigureAwait(false);
            }
        }
        finally
        {
            _progress.EndFile(entry.Length);
        }
    }

    /// <summary>Copies one file with retries; on success applies permissions, deletes the source of a move, and journals it done.</summary>
    private async Task CopyFileAsync(TreeItem item, string target, string targetKey, bool replace)
    {
        var entry = item.Entry;
        var source = entry.FullPath;
        var options = CopierOptions;
        var targetFolderKey = Path.GetDirectoryName(targetKey) ?? string.Empty;
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
                ReplaceExisting = replace,
                TryBlockClone = _clone,
                BeforeCommit = async (part, hash, token) =>
                {
                    if (options.Verify)
                    {
                        _progress.SetPhase("Verifying");
                        try
                        {
                            if (hash is null || !await Verifier.MatchesAsync(_fs, part, hash, options.HashAlgorithm, token).ConfigureAwait(false))
                            {
                                verifyFailed = true;
                                return new Problem(source, IoErrorKind.Other, CopyMessages.VerifyMismatch);
                            }
                        }
                        catch (FileSystemException ex)
                        {
                            verifyFailed = true;
                            return new Problem(source, ex.Kind, CopyMessages.VerifyCouldNotRead);
                        }
                        finally
                        {
                            _progress.SetPhase("Copying");
                        }
                    }

                    if (_options.CopyAlternateStreams)
                    {
                        try
                        {
                            _fs.CopyAlternateStreams(source, part);
                        }
                        catch (FileSystemException ex)
                        {
                            warnings.Add(new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.StreamsNotCopied));
                        }
                    }

                    return null;
                },
            };

            outcome = await _owner.Copier.CopyAsync(source, target, options, _pause, _progress.FileSink(), hooks, _ct).ConfigureAwait(false);
            if (outcome.Success || attempt >= TreeCopier.RetryDelays.Length || !IsWorthRetrying(outcome.Problem!.Kind))
            {
                break;
            }

            _progress.ResetCurrentFile();
            await Task.Delay(TreeCopier.RetryDelays[attempt], _owner.Time, _ct).ConfigureAwait(false);
        }

        if (!outcome.Success)
        {
            _outcome.VerifyFailed |= verifyFailed;
            var p = outcome.Problem!;
            Fail(item, p with { Path = TreePaths.Display(p.Path) });
            return;
        }

        if (options.Verify)
        {
            _outcome.Verified++; // BeforeCommit only lets a matching copy through
        }

        _outcome.Problems.AddRange(warnings);
        if (outcome.Problem is { } warning)
        {
            _outcome.Problems.Add(warning with { Path = TreePaths.Display(warning.Path) });
        }

        if (_options.CopyPermissions)
        {
            CopyPermissions(source, target);
        }

        if (_isMove)
        {
            try
            {
                _fs.Delete(source); // only now: the copy is complete and matched
            }
            catch (FileSystemException ex)
            {
                _outcome.Problems.Add(new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.OriginalNotRemoved));
            }
        }

        var hex = outcome.SourceHash is null ? null : Convert.ToHexStringLower(outcome.SourceHash);
        if (_options.ExportChecksums && hex is not null)
        {
            _checksums.Add(hex, targetKey);
        }

        _outcome.Copied++;
        _journal.Done(item.JournalKey, entry.Length, entry.LastWriteTimeUtc, hex);
    }

    private static bool IsWorthRetrying(IoErrorKind kind) =>
        kind is IoErrorKind.BadBlock or IoErrorKind.NetworkLost or IoErrorKind.SharingViolation or IoErrorKind.Other;

    /// <summary>
    /// A file renamed into place just before a crash (its recorded target holds exactly the source's data): re-apply metadata and
    /// permissions, finish a move, journal it done.
    /// </summary>
    private async Task<bool> TryRecoverAsync(TreeItem item)
    {
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

        if (_options.CopyPermissions)
        {
            CopyPermissions(item.Entry.FullPath, target);
        }

        if (_isMove)
        {
            TreePaths.TryDelete(_fs, item.Entry.FullPath); // the content was just checked against the recorded target
        }

        var hex = Convert.ToHexStringLower(found.Hash);
        if (_options.ExportChecksums)
        {
            _checksums.Add(hex, found.TargetKey);
        }

        _outcome.Copied++;
        _outcome.Verified++;
        _journal.Done(item.JournalKey, item.Entry.Length, item.Entry.LastWriteTimeUtc, hex);
        return true;
    }

    private void CopyPermissions(string source, string target)
    {
        try
        {
            _fs.CopySecurity(source, target);
        }
        catch (FileSystemException ex)
        {
            _outcome.PermissionProblem(target, ex);
        }
    }

    private void Fail(TreeItem item, Problem problem)
    {
        _outcome.Failed++;
        _outcome.Problems.Add(problem);
        _journal.Append(new JournalRecord(JournalRecordKind.FileFailed, item.JournalKey, item.Entry.Length, item.Entry.LastWriteTimeUtc, null, problem.Message));
        switch (_options.IfUnreadable)
        {
            case FailureRule.Stop:
                _outcome.Stopped = true;
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
            var (source, target) = _createdFolders[i];
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

            if (_options.CopyPermissions)
            {
                CopyPermissions(source.FullPath, target);
            }
        }

        foreach (var folder in _sourceFolders)
        {
            // Only empty folders go (Delete never removes contents): anything excluded, failed or left as a link keeps its folder.
            TreePaths.TryDelete(_fs, folder);
        }
    }

    private string WriteMessage(IoErrorKind kind) => CopyMessages.For(kind, whileWriting: true, CopyMessages.DriveOf(_destination));
}
