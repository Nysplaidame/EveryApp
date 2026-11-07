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
/// copy matched; only part files named in the journal are ever deleted on resume; links are never followed by a move.
/// </para>
/// <para>
/// Links: with FollowReparsePoints off, a link to a folder is skipped (and listed) when copying, and a link to a file is copied as the contents
/// of the file it points to (a link-keeping mode may come later). A move never follows links: on the same drive a link is moved as itself;
/// to another drive it is left in place and listed.
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

    private readonly IFileSystem _fs;
    private readonly DriveAwareScheduler _scheduler;
    private readonly IConflictResolver _resolver;
    private readonly string _jobsDir;
    private readonly ISleepBlocker _sleep;
    private readonly IDestinationInfo _destinationInfo;
    private readonly TimeProvider _time;
    private readonly FileCopier _copier;

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
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);
        _fs = fs;
        _scheduler = scheduler;
        _resolver = resolver;
        _jobsDir = jobsDir;
        _sleep = sleepBlocker ?? WindowsSleepBlocker.Instance;
        _destinationInfo = destinationInfo ?? WindowsDestinationInfo.Instance;
        _time = time ?? TimeProvider.System;
        _copier = new FileCopier(fs, BufferPool.Shared, _time);
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

        return new JobRun(this, jobId, spec, null, pause, progress, ct).RunAsync();
    }

    /// <summary>Continues a job from its journal: done files are skipped and part files the journal names are removed first.</summary>
    public Task<CopyResult> ResumeAsync(Guid jobId, PauseGate pause, IProgress<JobProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(progress);
        var journal = JobJournal.Open(_jobsDir, jobId, _time);
        try
        {
            if (journal.IsFinished)
            {
                journal.Dispose();
                return Task.FromResult(new CopyResult(0, 0, 0, [], [], AllVerified: true));
            }

            var spec = JobSpec.Parse(journal.OptionsJson);
            return new JobRun(this, jobId, spec, journal, pause, progress, ct).RunAsync();
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    private enum ItemKind
    {
        Folder,
        File,
        Link,
    }

    /// <summary>One source item (file or folder) given by the caller.</summary>
    private sealed record Root(string Path, FileEntry Entry, string Key, bool SameVolume, bool MoveByRename, List<Item> Items);

    /// <summary>One thing to do. <see cref="Key"/> is the path relative to the destination folder (also the journal key).</summary>
    private sealed record Item(ItemKind Kind, FileEntry Entry, string Key);

    /// <summary>The state of one run of one job.</summary>
    private sealed class JobRun(TreeCopier owner, Guid jobId, JobSpec spec, JobJournal? journal, PauseGate pause, IProgress<JobProgress> sink, CancellationToken ct)
    {
        private const uint NameSurrogateBit = 0x20000000;
        private const int MaxNameNumber = 9999;

        private readonly IFileSystem _fs = owner._fs;
        private readonly CopyOptions _options = spec.Options;
        private readonly bool _isMove = spec.Mode == CopyMode.Move;
        private readonly string _destination = spec.Destination;
        private readonly List<Problem> _problems = [];
        private readonly List<PreflightIssue> _issues = [];
        private readonly List<string> _checksums = [];
        private readonly List<(FileEntry Source, string Target)> _createdFolders = [];
        private readonly List<string> _sourceFolders = [];
        private readonly Dictionary<string, List<string>> _partsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _progressLock = new();
        private JobJournal? _journal = journal;
        private IReadOnlySet<string> _completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private SpeedWindow? _speed;
        private int _copied;
        private int _skipped;
        private int _failed;
        private bool _verifyMismatch;
        private bool _stopped;
        private bool _needsAdminReported;
        private bool _clone;
        private string _fileSystem = string.Empty;
        private long _bytesTotal;
        private long _bytesDone;
        private long _currentFileBytes;
        private int _filesTotal;
        private int _filesDone;
        private string _phase = "Checking";
        private string? _currentFile;

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
                _journal?.Dispose();
            }
        }

        private async Task<CopyResult> RunCoreAsync()
        {
            _speed = new SpeedWindow(owner._time.TimestampFrequency);
            if (_journal is not null)
            {
                _completed = _journal.CompletedPaths;
                foreach (var part in _journal.PartFiles)
                {
                    if (!_partsByKey.TryGetValue(part.RelativePath, out var list))
                    {
                        _partsByKey[part.RelativePath] = list = [];
                    }

                    list.Add(part.PartRelativePath);
                }
            }

            Report();

            // ---- Checking: overlap, walk, pre-flight. Nothing is written in this phase.
            var destinationEntry = NearestExisting(_destination);
            CheckOverlap();
            if (_issues.Count > 0)
            {
                return Finish(stopped: true, refused: true);
            }

            var roots = Plan(destinationEntry);
            RunPreflight(roots);
            if (_issues.Any(IsBlocking))
            {
                return Finish(stopped: true, refused: true);
            }

            _fileSystem = owner._destinationInfo.FileSystemOf(_destination);
            if (_options.DryRun)
            {
                return DryRun(roots);
            }

            // ---- Wait for the drive, keep awake, journal: all before the first write.
            var acquire = owner._scheduler.AcquireAsync(spec.Sources[0], _destination, ct);
            if (!acquire.IsCompleted)
            {
                SetPhase("Waiting");
            }

            var lease = await acquire.ConfigureAwait(false);
            try
            {
                using var awake = owner._sleep.Acquire();
                _journal ??= JobJournal.Create(owner._jobsDir, jobId, spec.ToJson(), owner._time);
                try
                {
                    _fs.CreateDirectory(_destination);
                }
                catch (FileSystemException ex)
                {
                    _problems.Add(new Problem(Display(_destination), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
                    return Finish(stopped: true, refused: true);
                }

                DeleteJournaledParts();
                SetPhase("Copying");

                foreach (var root in roots)
                {
                    if (_stopped)
                    {
                        break;
                    }

                    _clone = !_isMove && _fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase)
                        && destinationEntry is not null && root.Entry.VolumeSerial == destinationEntry.VolumeSerial;
                    await ProcessRootAsync(root).ConfigureAwait(false);
                }

                FinishFolders();
                WriteChecksums();
                if (!_stopped)
                {
                    Append(new JournalRecord(JournalRecordKind.JobFinished, null, 0, DateTime.UtcNow, null,
                        $"copied {_copied}, skipped {_skipped}, failed {_failed}"));
                }

                Report();
                return Finish(_stopped);
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>AllVerified: verification was on, no copy failed its check, and the job got past its checks (a refused job verified nothing).</summary>
        private CopyResult Finish(bool stopped, bool refused = false) =>
            new(_copied, _skipped, _failed, [.. _problems], [.. _issues], AllVerified: VerifyOn && !_verifyMismatch && !refused)
            {
                Stopped = stopped,
            };

        // ------------------------------------------------------------------ checking

        /// <summary>Refuses a destination inside a folder being copied, or an item that is already where it would go. Judged by on-disk identity.</summary>
        private void CheckOverlap()
        {
            foreach (var source in spec.Sources)
            {
                FileEntry identity;
                try
                {
                    identity = _fs.GetEntryFollowingLinks(source);
                }
                catch (FileSystemException)
                {
                    continue; // reported when the source is listed
                }

                if (identity.Attributes.HasFlag(FileAttributes.Directory)
                    && (PathSafety.IsWithin(source, _destination) || AnyAncestorIs(_destination, identity)))
                {
                    _issues.Add(new PreflightIssue(PreflightCode.DestinationInsideSource, Display(source), CopyMessages.DestinationInsideSource));
                    continue;
                }

                var name = NameOf(source);
                if (name.Length > 0 && TryGet(Path.Combine(_destination, name), follow: true) is { } target && SameObject(target, identity))
                {
                    _issues.Add(new PreflightIssue(PreflightCode.DestinationInsideSource, Display(source), CopyMessages.SameAsSource));
                }
            }
        }

        private bool AnyAncestorIs(string path, FileEntry identity)
        {
            for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if (TryGet(p, follow: true) is { } e && SameObject(e, identity))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameObject(FileEntry a, FileEntry b) => a.VolumeSerial == b.VolumeSerial && a.FileId == b.FileId;

        /// <summary>Lists every source and puts the work in order: each folder before its contents, small files first within a folder.</summary>
        private List<Root> Plan(FileEntry? destinationEntry)
        {
            var roots = new List<Root>();
            foreach (var source in spec.Sources)
            {
                FileEntry entry;
                try
                {
                    entry = _fs.GetEntry(source);
                }
                catch (FileSystemException ex)
                {
                    _failed++;
                    _problems.Add(new Problem(Display(source), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: false, CopyMessages.DriveOf(_destination))));
                    continue;
                }

                var key = NameOf(source);
                var isFolder = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (!isFolder && key.Length == 0)
                {
                    continue;
                }

                if (key.Length > 0 && _completed.Contains(key))
                {
                    continue; // moved as a whole before a crash
                }

                var sameVolume = destinationEntry is not null && entry.VolumeSerial == destinationEntry.VolumeSerial;
                if (_isMove && isFolder && !sameVolume && IsNameSurrogate(entry))
                {
                    _problems.Add(new Problem(Display(source), IoErrorKind.None, CopyMessages.LinkNotMovedAcrossDrives));
                    continue;
                }

                var items = new List<Item>();
                if (isFolder)
                {
                    if (key.Length > 0)
                    {
                        items.Add(new Item(ItemKind.Folder, entry, key));
                    }

                    items.AddRange(Walk(source, key));
                }
                else
                {
                    items.Add(new Item(ItemKind.File, entry, key));
                }

                var byRename = _isMove && sameVolume && isFolder && key.Length > 0 && _options.Exclude.Count == 0
                    && TryGet(Path.Combine(_destination, key), follow: false) is null;
                roots.Add(new Root(entry.FullPath, entry, key, _isMove && sameVolume, byRename, items));
            }

            return roots;
        }

        private List<Item> Walk(string root, string rootKey)
        {
            // A move never follows links, whatever the option says.
            var walker = new TreeWalker(_fs, followReparsePoints: !_isMove && _options.FollowReparsePoints, _options.Exclude);
            var folders = new List<Item>();
            var files = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);
            var links = new Dictionary<string, List<Item>>(StringComparer.OrdinalIgnoreCase);

            void AddTo(Dictionary<string, List<Item>> map, Item item)
            {
                var parent = Path.GetDirectoryName(item.Key) ?? string.Empty;
                if (!map.TryGetValue(parent, out var list))
                {
                    map[parent] = list = [];
                }

                list.Add(item);
            }

            Action<WalkItem>? onLink = _isMove ? link => AddTo(links, new Item(ItemKind.Link, link.Entry, Combine(rootKey, link.RelativePath))) : null;
            foreach (var w in walker.Walk(root, OnSkip, onLink))
            {
                var key = Combine(rootKey, w.RelativePath);
                if (w.Entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    folders.Add(new Item(ItemKind.Folder, w.Entry, key));
                }
                else
                {
                    AddTo(files, new Item(ItemKind.File, w.Entry, key));
                }
            }

            var ordered = new List<Item>();
            void AddContents(string folderKey)
            {
                if (files.TryGetValue(folderKey, out var list))
                {
                    ordered.AddRange(list.OrderBy(i => i.Entry.Length).ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase));
                }

                if (links.TryGetValue(folderKey, out var linkList))
                {
                    ordered.AddRange(linkList);
                }
            }

            AddContents(rootKey);
            foreach (var folder in folders)
            {
                ordered.Add(folder);
                AddContents(folder.Key);
            }

            return ordered;
        }

        private void OnSkip(WalkSkip skip) =>
            _problems.Add(new Problem(skip.Path, skip.Reason == CopyMessages.LinkSkipped ? IoErrorKind.None : IoErrorKind.Other, skip.Reason));

        private void RunPreflight(List<Root> roots)
        {
            var items = new List<WalkItem>();
            foreach (var root in roots.Where(r => !r.SameVolume))
            {
                foreach (var item in root.Items)
                {
                    if (item.Kind != ItemKind.Link && !_completed.Contains(item.Key))
                    {
                        items.Add(new WalkItem(item.Entry, item.Key));
                    }
                }
            }

            foreach (var root in roots)
            {
                foreach (var item in root.Items.Where(i => i.Kind == ItemKind.File && !_completed.Contains(i.Key)))
                {
                    _filesTotal++;
                    _bytesTotal += item.Entry.Length;
                }
            }

            if (items.Count > 0)
            {
                var fileSystem = owner._destinationInfo.FileSystemOf(_destination);
                _issues.AddRange(new PreflightRunner(_fs).Check(items, _destination, fileSystem, owner._destinationInfo.LongPathsEnabled));
            }

            Report();
        }

        /// <summary>Warnings let the copy go ahead: Ferry can still write the item, other programs may struggle with it.</summary>
        private static bool IsBlocking(PreflightIssue issue) =>
            issue.Code is not (PreflightCode.PathTooLong or PreflightCode.TrailingDotOrSpace or PreflightCode.FreeSpaceUnknown);

        // ------------------------------------------------------------------ dry run

        private CopyResult DryRun(List<Root> roots)
        {
            var plan = new List<PlannedItem>();
            var copied = 0;
            var skipped = 0;
            foreach (var root in roots)
            {
                foreach (var item in root.Items)
                {
                    var target = Path.Combine(_destination, item.Key);
                    var source = Display(item.Entry.FullPath);
                    if (item.Kind != ItemKind.File)
                    {
                        if (TryGet(target, follow: false) is null)
                        {
                            plan.Add(new PlannedItem(source, Display(target), item.Kind == ItemKind.Folder ? PlannedAction.CreateFolder : PlannedAction.Move));
                        }

                        continue;
                    }

                    var (action, to) = PlanFile(item.Entry, target);
                    plan.Add(new PlannedItem(source, Display(to), action));
                    if (action == PlannedAction.Skip)
                    {
                        skipped++;
                    }
                    else if (action != PlannedAction.Ask)
                    {
                        copied++;
                    }
                }
            }

            return new CopyResult(copied, skipped, _failed, [.. _problems], [.. _issues], AllVerified: false) { Plan = plan };
        }

        private (PlannedAction Action, string Target) PlanFile(FileEntry source, string target)
        {
            var existing = TryGet(target, follow: false);
            if (existing is null)
            {
                return (_isMove ? PlannedAction.Move : PlannedAction.Copy, target);
            }

            return _options.IfExists switch
            {
                ConflictRule.Ask => (PlannedAction.Ask, target),
                ConflictRule.Overwrite => (PlannedAction.Replace, target),
                ConflictRule.OverwriteIfNewer => source.LastWriteTimeUtc > existing.LastWriteTimeUtc ? (PlannedAction.Replace, target) : (PlannedAction.Skip, target),
                ConflictRule.Rename => (PlannedAction.CopyWithNewName, NextFreeName(target) ?? target),
                ConflictRule.KeepBoth => (PlannedAction.KeepBoth, target),
                _ => (PlannedAction.Skip, target),
            };
        }

        // ------------------------------------------------------------------ copying

        /// <summary>Resume: removes exactly the part files the journal names, for files not done yet. Never deletes by pattern.</summary>
        private void DeleteJournaledParts()
        {
            foreach (var (key, parts) in _partsByKey)
            {
                if (_completed.Contains(key))
                {
                    continue;
                }

                foreach (var part in parts)
                {
                    TryDelete(Path.Combine(_destination, part));
                }
            }
        }

        private async Task ProcessRootAsync(Root root)
        {
            if (root.MoveByRename)
            {
                MoveWhole(root.Entry.FullPath, root.Key, root.Items.Count(i => i.Kind == ItemKind.File), root.Items.Where(i => i.Kind == ItemKind.File).Sum(i => i.Entry.Length));
                return;
            }

            var skipUnder = new List<string>();
            foreach (var item in root.Items)
            {
                if (_stopped)
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();
                if (skipUnder.Exists(prefix => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                switch (item.Kind)
                {
                    case ItemKind.Folder:
                        if (!ProcessFolder(root, item))
                        {
                            skipUnder.Add(item.Key + "\\");
                        }

                        break;
                    case ItemKind.Link:
                        ProcessLink(root, item);
                        break;
                    default:
                        await ProcessFileAsync(root, item).ConfigureAwait(false);
                        break;
                }
            }
        }

        /// <summary>Same-drive move of a whole folder: one rename. The folder's files then count as moved.</summary>
        private void MoveWhole(string source, string key, int files, long bytes)
        {
            try
            {
                _fs.Move(source, Path.Combine(_destination, key), overwrite: false);
                Append(Done(key, 0, DateTime.UtcNow, null));
                _copied += files;
                CompleteFiles(files, bytes);
            }
            catch (FileSystemException ex)
            {
                _failed += files;
                _problems.Add(new Problem(Display(source), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
                CompleteFiles(files, bytes);
            }
        }

        /// <summary>Returns false when the folder's contents must be skipped (they moved with it, or it could not be created).</summary>
        private bool ProcessFolder(Root root, Item item)
        {
            var target = Path.Combine(_destination, item.Key);
            var existing = TryGet(target, follow: false);
            var (files, bytes) = CountUnder(root, item.Key);
            if (existing is not null && !existing.Attributes.HasFlag(FileAttributes.Directory))
            {
                _failed += files;
                _problems.Add(new Problem(Display(item.Entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.FileInTheWay));
                CompleteFiles(files, bytes);
                return false;
            }

            if (existing is null && root.SameVolume && _options.Exclude.Count == 0 && item.Key != root.Key)
            {
                MoveWhole(item.Entry.FullPath, item.Key, files, bytes);
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
                _failed += files;
                _problems.Add(new Problem(Display(target), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
                CompleteFiles(files, bytes);
                return false;
            }
        }

        private static (int Files, long Bytes) CountUnder(Root root, string folderKey)
        {
            var prefix = folderKey + "\\";
            var files = root.Items.Where(i => i.Kind == ItemKind.File && i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            return (files.Count, files.Sum(i => i.Entry.Length));
        }

        /// <summary>A folder link inside a move: renamed as the link itself on the same drive, left in place otherwise.</summary>
        private void ProcessLink(Root root, Item item)
        {
            if (!root.SameVolume)
            {
                _problems.Add(new Problem(Display(item.Entry.FullPath), IoErrorKind.None, CopyMessages.LinkNotMovedAcrossDrives));
                return;
            }

            var target = Path.Combine(_destination, item.Key);
            try
            {
                if (TryGet(target, follow: false) is not null)
                {
                    throw new FileSystemException(target, IoErrorKind.AlreadyExists, null);
                }

                _fs.Move(item.Entry.FullPath, target, overwrite: false);
            }
            catch (FileSystemException ex)
            {
                _problems.Add(new Problem(Display(item.Entry.FullPath), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
            }
        }

        private async Task ProcessFileAsync(Root root, Item item)
        {
            var entry = item.Entry;
            if (_completed.Contains(item.Key))
            {
                return;
            }

            await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            StartFile(item.Key);
            try
            {
                if (_isMove && !root.SameVolume && IsNameSurrogate(entry))
                {
                    _problems.Add(new Problem(Display(entry.FullPath), IoErrorKind.None, CopyMessages.FileLinkNotMovedAcrossDrives));
                    return;
                }

                if (await TryRecoverAsync(item, root).ConfigureAwait(false))
                {
                    return;
                }

                var target = Path.Combine(_destination, item.Key);
                var targetKey = item.Key;
                var existing = TryGet(target, follow: false);
                var replace = false;
                string? keepOldAs = null;
                if (existing is not null)
                {
                    if (existing.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        Fail(item, new Problem(Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.FolderInTheWay));
                        return;
                    }

                    switch (await DecideAsync(entry, existing).ConfigureAwait(false))
                    {
                        case ConflictRule.Overwrite:
                            replace = true;
                            break;
                        case ConflictRule.Rename:
                            var renamed = NextFreeName(target);
                            if (renamed is null)
                            {
                                Fail(item, new Problem(Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.NoFreeName));
                                return;
                            }

                            target = renamed;
                            targetKey = Combine(Path.GetDirectoryName(item.Key) ?? string.Empty, Path.GetFileName(renamed));
                            break;
                        case ConflictRule.KeepBoth:
                            keepOldAs = NextFreeName(target);
                            if (keepOldAs is null)
                            {
                                Fail(item, new Problem(Display(entry.FullPath), IoErrorKind.AlreadyExists, CopyMessages.NoFreeName));
                                return;
                            }

                            break;
                        default:
                            _skipped++;
                            Append(new JournalRecord(JournalRecordKind.FileSkipped, item.Key, entry.Length, entry.LastWriteTimeUtc, null, null));
                            return;
                    }
                }

                if (root.SameVolume)
                {
                    MoveByRename(item, target, replace, keepOldAs);
                }
                else
                {
                    await CopyAsync(item, target, targetKey, replace, keepOldAs).ConfigureAwait(false);
                }
            }
            finally
            {
                EndFile(entry.Length);
            }
        }

        private async ValueTask<ConflictRule> DecideAsync(FileEntry source, FileEntry existing)
        {
            var rule = _options.IfExists;
            if (rule == ConflictRule.Ask)
            {
                rule = await owner._resolver.ResolveAsync(source, existing, ct).ConfigureAwait(false);
            }

            return rule switch
            {
                ConflictRule.OverwriteIfNewer => source.LastWriteTimeUtc > existing.LastWriteTimeUtc ? ConflictRule.Overwrite : ConflictRule.Skip,
                ConflictRule.Overwrite or ConflictRule.Rename or ConflictRule.KeepBoth => rule,
                _ => ConflictRule.Skip, // Skip, or Ask answered with Ask
            };
        }

        /// <summary>Same-drive move of one file: a rename, so the data never changes and needs no verify.</summary>
        private void MoveByRename(Item item, string target, bool replace, string? keepOldAs)
        {
            var source = item.Entry.FullPath;
            try
            {
                if (keepOldAs is not null)
                {
                    _fs.Move(target, keepOldAs, overwrite: false);
                    try
                    {
                        _fs.Move(source, target, overwrite: false);
                    }
                    catch (FileSystemException)
                    {
                        TryMove(keepOldAs, target);
                        throw;
                    }
                }
                else if (replace)
                {
                    var old = _fs.GetEntry(target);
                    if (old.Attributes.HasFlag(FileAttributes.ReadOnly))
                    {
                        _fs.SetMetadata(target, old.Attributes & ~FileAttributes.ReadOnly, old.CreationTimeUtc, old.LastWriteTimeUtc);
                    }

                    try
                    {
                        _fs.Move(source, target, overwrite: true);
                    }
                    catch (FileSystemException)
                    {
                        TrySetMetadata(target, old);
                        throw;
                    }
                }
                else
                {
                    _fs.Move(source, target, overwrite: false);
                }

                _copied++;
                Append(Done(item.Key, item.Entry.Length, item.Entry.LastWriteTimeUtc, null));
            }
            catch (FileSystemException ex)
            {
                Fail(item, new Problem(Display(source), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
            }
        }

        /// <summary>Copies one file with retries; on success applies permissions, deletes the source of a move, and journals it done.</summary>
        private async Task CopyAsync(Item item, string target, string targetKey, bool replace, string? keepOldAs)
        {
            var entry = item.Entry;
            var source = entry.FullPath;
            var options = CopierOptions;
            var targetFolderKey = Path.GetDirectoryName(targetKey) ?? string.Empty;
            FileCopyOutcome outcome;
            var mismatch = false;
            var warnings = new List<Problem>();
            for (var attempt = 0; ; attempt++)
            {
                mismatch = false;
                warnings.Clear();
                var oldMoved = false;
                var hooks = new FileCopyHooks
                {
                    PartChosen = part => Append(new JournalRecord(
                        JournalRecordKind.FilePartStarted, item.Key, entry.Length, entry.LastWriteTimeUtc, null, Combine(targetFolderKey, Path.GetFileName(part)))),
                    ReplaceExisting = replace,
                    TryBlockClone = _clone,
                    BeforeCommit = async (part, hash, token) =>
                    {
                        if (options.Verify)
                        {
                            SetPhase("Verifying");
                            try
                            {
                                if (hash is null || !await Verifier.MatchesAsync(_fs, part, hash, options.HashAlgorithm, token).ConfigureAwait(false))
                                {
                                    mismatch = true;
                                    return new Problem(source, IoErrorKind.Other, CopyMessages.VerifyMismatch);
                                }
                            }
                            catch (FileSystemException ex)
                            {
                                return new Problem(source, ex.Kind, CopyMessages.VerifyCouldNotRead);
                            }
                            finally
                            {
                                SetPhase("Copying");
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
                                warnings.Add(new Problem(Display(source), ex.Kind, CopyMessages.StreamsNotCopied));
                            }
                        }

                        if (keepOldAs is not null)
                        {
                            _fs.Move(target, keepOldAs, overwrite: false); // the old file steps aside only once the new one is complete
                            oldMoved = true;
                        }

                        return null;
                    },
                };

                outcome = await owner._copier.CopyAsync(source, target, options, pause, new FileProgressSink(this), hooks, ct).ConfigureAwait(false);
                if (!outcome.Success && oldMoved)
                {
                    TryMove(keepOldAs!, target); // the new copy did not land: the old file gets its name back
                }

                if (outcome.Success || attempt >= RetryDelays.Length || !IsWorthRetrying(outcome.Problem!.Kind))
                {
                    break;
                }

                ResetCurrentFile();
                await Task.Delay(RetryDelays[attempt], owner._time, ct).ConfigureAwait(false);
            }

            if (!outcome.Success)
            {
                _verifyMismatch |= mismatch;
                var p = outcome.Problem!;
                Fail(item, p with { Path = Display(p.Path) });
                return;
            }

            _problems.AddRange(warnings);
            if (outcome.Problem is { } warning)
            {
                _problems.Add(warning with { Path = Display(warning.Path) });
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
                    _problems.Add(new Problem(Display(source), ex.Kind, CopyMessages.OriginalNotRemoved));
                }
            }

            var hex = outcome.SourceHash is null ? null : Convert.ToHexStringLower(outcome.SourceHash);
            if (_options.ExportChecksums && hex is not null)
            {
                _checksums.Add($"{hex}  {targetKey}");
            }

            _copied++;
            Append(Done(item.Key, entry.Length, entry.LastWriteTimeUtc, hex));
        }

        private static bool IsWorthRetrying(IoErrorKind kind) =>
            kind is IoErrorKind.BadBlock or IoErrorKind.NetworkLost or IoErrorKind.SharingViolation or IoErrorKind.Other;

        /// <summary>
        /// Resume after a crash between the rename and the done record: when this file has a part record and the destination named by it holds
        /// exactly the source's data, it is finished (metadata re-applied) rather than copied again.
        /// </summary>
        private async Task<bool> TryRecoverAsync(Item item, Root root)
        {
            if (root.SameVolume || !_partsByKey.TryGetValue(item.Key, out var parts))
            {
                return false;
            }

            const int PartTail = 1 + 8 + 10; // ".<8 hex>.ferrypart"
            foreach (var part in parts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (part.Length <= PartTail)
                {
                    continue;
                }

                var targetKey = part[..^PartTail];
                var target = Path.Combine(_destination, targetKey);
                if (TryGet(target, follow: false) is not { } existing || existing.Attributes.HasFlag(FileAttributes.Directory) || existing.Length != item.Entry.Length)
                {
                    continue;
                }

                try
                {
                    var algorithm = _options.HashAlgorithm;
                    var source = await Verifier.HashAsync(_fs, item.Entry.FullPath, unbuffered: false, algorithm, ct).ConfigureAwait(false);
                    if (!await Verifier.MatchesAsync(_fs, target, source, algorithm, ct).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (_options.KeepDatesAndAttributes)
                    {
                        var e = _fs.GetEntryFollowingLinks(item.Entry.FullPath);
                        var attributes = e.Attributes & FileCopier.CopiedAttributes;
                        _fs.SetMetadata(target, attributes == 0 ? FileAttributes.Normal : attributes, e.CreationTimeUtc, e.LastWriteTimeUtc);
                    }

                    if (_options.CopyPermissions)
                    {
                        CopyPermissions(item.Entry.FullPath, target);
                    }

                    if (_isMove)
                    {
                        TryDelete(item.Entry.FullPath);
                    }

                    var hex = Convert.ToHexStringLower(source);
                    if (_options.ExportChecksums)
                    {
                        _checksums.Add($"{hex}  {targetKey}");
                    }

                    _copied++;
                    Append(Done(item.Key, item.Entry.Length, item.Entry.LastWriteTimeUtc, hex));
                    return true;
                }
                catch (FileSystemException)
                {
                    // Could not check it: copy normally (the conflict rule then decides).
                }
            }

            return false;
        }

        private void CopyPermissions(string source, string target)
        {
            try
            {
                _fs.CopySecurity(source, target);
            }
            catch (FileSystemException ex) when (ex.Kind == IoErrorKind.AccessDenied)
            {
                // One clear piece of advice for the whole job rather than the same line for every file.
                if (!_needsAdminReported)
                {
                    _needsAdminReported = true;
                    _problems.Add(new Problem(Display(target), ex.Kind, CopyMessages.NeedsAdministrator));
                }
            }
            catch (FileSystemException ex)
            {
                _problems.Add(new Problem(Display(target), ex.Kind, CopyMessages.PermissionsNotCopied));
            }
        }

        private void Fail(Item item, Problem problem)
        {
            _failed++;
            _problems.Add(problem);
            Append(new JournalRecord(JournalRecordKind.FileFailed, item.Key, item.Entry.Length, item.Entry.LastWriteTimeUtc, null, problem.Message));
            if (_options.IfUnreadable == FailureRule.Stop)
            {
                // FailureRule.Ask has no question interface yet (M2 UI): it behaves like RetryThenSkipAndList.
                _stopped = true;
                _problems.Add(new Problem(problem.Path, IoErrorKind.None, CopyMessages.StoppedAfterProblem));
            }
        }

        // ------------------------------------------------------------------ finishing

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
                TryDelete(folder);
            }
        }

        /// <summary>Writes &lt;destination&gt;\ferry-checksums.&lt;algorithm&gt; ("hash  relative-path" lines) through a part file, never over an existing file.</summary>
        private void WriteChecksums()
        {
            if (!_options.ExportChecksums || _checksums.Count == 0)
            {
                return;
            }

            var name = "ferry-checksums." + _options.HashAlgorithm.ToString().ToLowerInvariant();
            var target = Path.Combine(_destination, name);
            if (TryGet(target, follow: false) is not null)
            {
                target = NextFreeName(target) ?? target;
            }

            var part = FileCopier.NewPartPath(target);
            Append(new JournalRecord(JournalRecordKind.FilePartStarted, Path.GetFileName(target), 0, DateTime.UtcNow, null, Path.GetFileName(part)));
            var data = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', _checksums) + "\n");
            var created = false;
            try
            {
                var writer = _fs.CreateWrite(part, 0, unbuffered: false);
                created = true;
                try
                {
                    writer.WriteAsync(data, 0, ct).AsTask().GetAwaiter().GetResult();
                    writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                _fs.Move(part, target, overwrite: false);
                created = false;
            }
            catch (FileSystemException ex)
            {
                _problems.Add(new Problem(Display(target), ex.Kind, CopyMessages.ChecksumsNotWritten));
            }
            finally
            {
                if (created)
                {
                    TryDelete(part);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Append(JournalRecord record)
        {
            try
            {
                _journal?.Append(record);
            }
            catch (ArgumentException)
            {
                // A name the journal cannot hold safely: the file is simply redone on resume (copies never land half-written).
            }
        }

        private static JournalRecord Done(string key, long length, DateTime lastWrite, string? hash) =>
            new(JournalRecordKind.FileDone, key, length, lastWrite, hash, null);

        /// <summary>"name (2).ext", "name (3).ext"... the first one not taken. Null if none is free.</summary>
        private string? NextFreeName(string target)
        {
            var folder = Path.GetDirectoryName(target) ?? string.Empty;
            var name = Path.GetFileName(target);
            var dot = name.LastIndexOf('.');
            var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
            for (var n = 2; n <= MaxNameNumber; n++)
            {
                var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
                if (TryGetOrTaken(candidate))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>True when the name is taken, or cannot be checked (then it is treated as taken: never risk using someone's name).</summary>
        private bool TryGetOrTaken(string path)
        {
            try
            {
                _fs.GetEntry(path);
                return true;
            }
            catch (FileSystemException ex)
            {
                return ex.Kind != IoErrorKind.NotFound;
            }
        }

        private FileEntry? TryGet(string path, bool follow)
        {
            try
            {
                return follow ? _fs.GetEntryFollowingLinks(path) : _fs.GetEntry(path);
            }
            catch (FileSystemException)
            {
                return null;
            }
        }

        private FileEntry? NearestExisting(string path)
        {
            for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if (TryGet(p, follow: true) is { } e)
                {
                    return e;
                }
            }

            return null;
        }

        private void TryDelete(string path)
        {
            try
            {
                _fs.Delete(path);
            }
            catch (FileSystemException)
            {
                // Already gone, or not empty: either way nothing to do.
            }
        }

        private void TryMove(string from, string to)
        {
            try
            {
                _fs.Move(from, to, overwrite: false);
            }
            catch (FileSystemException ex)
            {
                _problems.Add(new Problem(Display(from), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: true, CopyMessages.DriveOf(_destination))));
            }
        }

        private void TrySetMetadata(string path, FileEntry entry)
        {
            try
            {
                _fs.SetMetadata(path, entry.Attributes, entry.CreationTimeUtc, entry.LastWriteTimeUtc);
            }
            catch (FileSystemException)
            {
                // Best effort: only the ReadOnly flag may be off.
            }
        }

        private static bool IsNameSurrogate(FileEntry entry) => entry.IsReparsePoint && (entry.ReparseTag & NameSurrogateBit) != 0;

        /// <summary>The last path segment, or "" for a drive or share root (whose contents go straight into the destination).</summary>
        private static string NameOf(string path)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 0 || trimmed.EndsWith(':') || trimmed.StartsWith(@"\\?", StringComparison.Ordinal) && trimmed.Length <= 4)
            {
                return string.Empty;
            }

            var name = Path.GetFileName(trimmed);
            return name.EndsWith(':') ? string.Empty : name;
        }

        private static string Combine(string folderKey, string relative) => folderKey.Length == 0 ? relative : folderKey + "\\" + relative;

        private static string Display(string path) => PathDisplay.ToDisplayPath(path);

        // ------------------------------------------------------------------ progress

        private void SetPhase(string phase)
        {
            lock (_progressLock)
            {
                _phase = phase;
            }

            Report();
        }

        private void StartFile(string key)
        {
            lock (_progressLock)
            {
                _currentFile = key;
                _currentFileBytes = 0;
            }

            Report();
        }

        private void ResetCurrentFile()
        {
            lock (_progressLock)
            {
                _currentFileBytes = 0;
            }
        }

        private void EndFile(long length)
        {
            lock (_progressLock)
            {
                _currentFileBytes = 0;
                _bytesDone += length;
                _filesDone++;
            }

            Report();
        }

        private void CompleteFiles(int files, long bytes)
        {
            lock (_progressLock)
            {
                _bytesDone += bytes;
                _filesDone += files;
            }

            Report();
        }

        private void FileBytes(long done)
        {
            lock (_progressLock)
            {
                _currentFileBytes = done;
            }

            Report();
        }

        private void Report()
        {
            JobProgress snapshot;
            lock (_progressLock)
            {
                var done = _bytesDone + _currentFileBytes;
                _speed!.Add(owner._time.GetTimestamp(), done);
                var speed = _speed.BytesPerSecond;
                TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _bytesTotal - done) / speed) : null;
                snapshot = new JobProgress(done, _bytesTotal, _filesDone, _filesTotal, speed, remaining, _currentFile, _phase);
            }

#pragma warning disable CA1031 // The progress callback belongs to the caller; it must never break the copy.
            try
            {
                sink.Report(snapshot);
            }
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        private sealed class FileProgressSink(JobRun run) : IProgress<FileProgress>
        {
            public void Report(FileProgress value) => run.FileBytes(value.Done);
        }
    }
}
