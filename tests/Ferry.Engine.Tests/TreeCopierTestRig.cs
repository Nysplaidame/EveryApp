using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Scheduling;
using EveryApp.Core.Tests.Fakes;

namespace Ferry.Engine.Tests;

/// <summary>In-memory disks (C: and D: are different volumes) behind fault injection and an operation log, plus fakes for every seam.</summary>
internal sealed class TreeCopierRig : IDisposable
{
    public TreeCopierRig(string? jobsDir = null)
    {
        Mem = new InMemoryFileSystem { SectorSize = 4096 };
        Faults = new FaultInjectingFileSystem(Mem);
        Fs = new RecordingFileSystem(Faults);
        JobsDir = jobsDir ?? Path.Combine(Path.GetTempPath(), "ferry-jobs-" + Guid.NewGuid().ToString("N"));
        Devices = new FakeDeviceResolver();
        Scheduler = new DriveAwareScheduler(Devices);
    }

    public InMemoryFileSystem Mem { get; }

    public FaultInjectingFileSystem Faults { get; }

    public RecordingFileSystem Fs { get; }

    public string JobsDir { get; }

    public FakeDeviceResolver Devices { get; }

    public DriveAwareScheduler Scheduler { get; }

    public FakeSleepBlocker Sleep { get; } = new();

    public FakeDestinationInfo Destination { get; } = new();

    public ScriptedResolver Resolver { get; } = new();

    public FakeElevation Elevation { get; } = new();

    public FakeConfirmation Confirmation { get; } = new();

    public TreeCopier NewCopier() => new(Fs, Scheduler, Resolver, JobsDir, Sleep, Destination, new AutoAdvancingTime(), Elevation);

    /// <summary>Resumes the job with <see cref="Confirmation"/> answering the question.</summary>
    public Task<CopyResult> ResumeAsync(Guid jobId, IProgress<JobProgress>? progress = null) =>
        NewCopier().ResumeAsync(jobId, Confirmation, new PauseGate(), progress ?? new ListProgress(), TestContext.Current.CancellationToken);

    public Task<CopyResult> RunAsync(
        string[] sources, string destination, CopyOptions? options = null, CopyMode mode = CopyMode.Copy, IProgress<JobProgress>? progress = null,
        Guid? jobId = null, CancellationToken? ct = null) =>
        NewCopier().RunAsync(
            jobId ?? Guid.NewGuid(), sources, destination, mode, options ?? new CopyOptions(), new PauseGate(), progress ?? new ListProgress(),
            ct ?? TestContext.Current.CancellationToken);

    public bool JournalExists(Guid id) => File.Exists(Path.Combine(JobsDir, id.ToString("N") + ".journal"));

    public bool AnyJournal() => Directory.Exists(JobsDir) && Directory.EnumerateFiles(JobsDir).Any();

    /// <summary>Every *.ferrypart file under <paramref name="root"/> (searched recursively).</summary>
    public List<string> PartFiles(string root)
    {
        var found = new List<string>();
        if (!Mem.Exists(root))
        {
            return found;
        }

        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            foreach (var child in Mem.EnumerateChildren(pending.Pop()))
            {
                if (child.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(child.FullPath);
                }
                else if (child.FullPath.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(child.FullPath);
                }
            }
        }

        return found;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(JobsDir))
            {
                Directory.Delete(JobsDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Synchronous progress sink that keeps every report.</summary>
internal sealed class ListProgress : IProgress<JobProgress>
{
    private readonly Lock _lock = new();
    private readonly List<JobProgress> _reports = [];

    public Action<JobProgress>? OnReport { get; set; }

    public List<JobProgress> Reports
    {
        get
        {
            lock (_lock)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(JobProgress value)
    {
        lock (_lock)
        {
            _reports.Add(value);
        }

        OnReport?.Invoke(value);
    }
}

internal sealed class FakeSleepBlocker : ISleepBlocker
{
    private int _active;

    public int Acquired { get; private set; }

    public int Released { get; private set; }

    public int Active => Volatile.Read(ref _active);

    public IDisposable Acquire()
    {
        Acquired++;
        Interlocked.Increment(ref _active);
        return new Lease(this);
    }

    private sealed class Lease(FakeSleepBlocker owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                owner.Released++;
                Interlocked.Decrement(ref owner._active);
            }
        }
    }
}

internal sealed class FakeDestinationInfo : IDestinationInfo
{
    public string FileSystem { get; set; } = "NTFS";

    public bool LongPathsEnabled { get; set; } = true;

    public bool FixedLocal { get; set; } = true;

    /// <summary>Free bytes reported for the jobs folder's drive (null: unknown).</summary>
    public long? JobsFreeBytes { get; set; } = 1L << 40;

    public string FileSystemOf(string path) => FileSystem;

    public bool IsFixedLocalDrive(string path) => FixedLocal;

    public long? FreeBytes(string path) => JobsFreeBytes;
}

internal sealed class FakeElevation : IElevationCheck
{
    public bool IsElevated { get; set; }
}

/// <summary>Answers the resume question with <see cref="Answer"/> and keeps what it was shown.</summary>
internal sealed class FakeConfirmation : IResumeConfirmation
{
    public bool Answer { get; set; } = true;

    public List<ResumeRequest> Asked { get; } = [];

    public ValueTask<bool> ConfirmAsync(ResumeRequest request, CancellationToken ct)
    {
        Asked.Add(request);
        return ValueTask.FromResult(Answer);
    }
}

/// <summary>Answers every conflict question with <see cref="Answer"/> and records the questions.</summary>
internal sealed class ScriptedResolver : IConflictResolver
{
    public ConflictRule Answer { get; set; } = ConflictRule.Skip;

    public List<(FileEntry Source, FileEntry Existing)> Questions { get; } = [];

    public ValueTask<ConflictRule> ResolveAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        Questions.Add((source, existing));
        return ValueTask.FromResult(Answer);
    }
}

/// <summary>Thrown by a test hook to simulate the process dying at that point (not an I/O error, so nothing handles it).</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("Simulated crash.")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Logs every operation that touches the disk (plain paths, no \\?\ prefix) and offers hooks: corrupt or fail writes, fail deletes,
/// run code when a file is created or its metadata set, and rewrite entries (e.g. to make two drive letters one volume).
/// </summary>
internal sealed class RecordingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];

    /// <summary>Returns the bytes to really write for a write to (path, offset), e.g. with a flipped bit.</summary>
    public Func<string, long, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? TransformWrite { get; set; }

    /// <summary>Returns an exception to throw for a write to (path, offset), or null.</summary>
    public Func<string, long, Exception?>? OnWrite { get; set; }

    /// <summary>Returns an exception to throw for opening (path, unbuffered) to read, or null.</summary>
    public Func<string, bool, Exception?>? OnOpenRead { get; set; }

    /// <summary>Runs just before CreateWrite (e.g. to swap a folder for a link after the folder was checked).</summary>
    public Action<string>? BeforeCreateWrite { get; set; }

    /// <summary>Runs just before a verified delete is attempted (to change the item under the job's feet).</summary>
    public Action<string>? BeforeDeleteVerified { get; set; }

    /// <summary>Returns a volume id to report instead of the real one, or null to ask the inner file system.</summary>
    public Func<string, string?>? VolumeIdOverride { get; set; }

    /// <summary>Runs after a file is created by CreateWrite.</summary>
    public Action<string>? AfterCreateWrite { get; set; }

    /// <summary>Returns an exception to throw instead of deleting, or null.</summary>
    public Func<string, Exception?>? OnDelete { get; set; }

    /// <summary>Runs before SetMetadata (may throw).</summary>
    public Action<string>? BeforeSetMetadata { get; set; }

    /// <summary>Rewrites every entry handed out.</summary>
    public Func<FileEntry, FileEntry>? MapEntry { get; set; }

    public List<string> Log
    {
        get
        {
            lock (_lock)
            {
                return [.. _log];
            }
        }
    }

    public int Count(string operation) => Log.Count(l => l.StartsWith(operation + " ", StringComparison.Ordinal));

    public void ClearLog()
    {
        lock (_lock)
        {
            _log.Clear();
        }
    }

    public static string Plain(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + p[8..];
        }

        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    public IReadHandle OpenRead(string path, bool unbuffered)
    {
        Add($"OpenRead {Plain(path)}{(unbuffered ? " unbuffered" : string.Empty)}");
        if (OnOpenRead?.Invoke(path, unbuffered) is { } ex)
        {
            throw ex;
        }

        return inner.OpenRead(path, unbuffered);
    }

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered)
    {
        Add($"CreateWrite {Plain(path)}");
        BeforeCreateWrite?.Invoke(path);
        var handle = new WriteHandle(this, path, inner.CreateWrite(path, preallocate, unbuffered));
        AfterCreateWrite?.Invoke(path);
        return handle;
    }

    public FileEntry GetEntry(string path) => Map(inner.GetEntry(path));

    public FileEntry GetEntryFollowingLinks(string path) => Map(inner.GetEntryFollowingLinks(path));

    public IEnumerable<FileEntry> EnumerateChildren(string directory) => MapEntry is null ? inner.EnumerateChildren(directory) : inner.EnumerateChildren(directory).Select(MapEntry);

    public void CreateDirectory(string path)
    {
        Add($"CreateDirectory {Plain(path)}");
        inner.CreateDirectory(path);
    }

    public void Move(string source, string destination, bool overwrite)
    {
        Add($"Move {Plain(source)} -> {Plain(destination)}{(overwrite ? " overwrite" : string.Empty)}");
        inner.Move(source, destination, overwrite);
    }

    public void Delete(string path)
    {
        Add($"Delete {Plain(path)}");
        if (OnDelete?.Invoke(path) is { } ex)
        {
            throw ex;
        }

        inner.Delete(path);
    }

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc)
    {
        BeforeSetMetadata?.Invoke(path);
        Add($"SetMetadata {Plain(path)}");
        inner.SetMetadata(path, attributes, creationUtc, lastWriteUtc);
    }

    public long GetFreeSpace(string path) => inner.GetFreeSpace(path);

    public int GetSectorSize(string path) => inner.GetSectorSize(path);

    public SecurityCopyResult CopySecurity(string source, string target)
    {
        Add($"CopySecurity {Plain(source)} -> {Plain(target)}");
        return inner.CopySecurity(source, target);
    }

    public void CopyAlternateStreams(string source, string target, bool allStreams, CancellationToken ct)
    {
        Add($"CopyStreams {Plain(source)} -> {Plain(target)}{(allStreams ? " all" : " marker")}");
        inner.CopyAlternateStreams(source, target, allStreams, ct);
    }

    public string GetFinalPath(string path) => inner.GetFinalPath(path);

    public string? GetVolumeId(string path) => VolumeIdOverride?.Invoke(path) ?? inner.GetVolumeId(path);

    public bool DeleteVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, long expectedLength, DateTime expectedLastWriteUtc, string? mustBeUnder)
    {
        Add($"DeleteVerified {Plain(path)}");
        BeforeDeleteVerified?.Invoke(path);
        return inner.DeleteVerified(path, expectedFileId, expectedVolumeSerial, expectedLength, expectedLastWriteUtc, mustBeUnder);
    }

    public bool DeleteFileContained(string path, string mustBeUnder)
    {
        Add($"DeleteContained {Plain(path)}");
        return inner.DeleteFileContained(path, mustBeUnder);
    }

    public bool IsContained(string path, string root) => inner.IsContained(path, root);

    public bool DeleteEmptyFolderVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, string? mustBeUnder)
    {
        Add($"DeleteFolderVerified {Plain(path)}");
        BeforeDeleteVerified?.Invoke(path);
        return inner.DeleteEmptyFolderVerified(path, expectedFileId, expectedVolumeSerial, mustBeUnder);
    }

    public bool TryCloneBlocks(string source, string target)
    {
        var ok = inner.TryCloneBlocks(source, target);
        Add($"Clone {Plain(source)} -> {Plain(target)} {(ok ? "ok" : "no")}");
        return ok;
    }

    private FileEntry Map(FileEntry entry) => MapEntry is null ? entry : MapEntry(entry);

    private void Add(string line)
    {
        lock (_lock)
        {
            _log.Add(line);
        }
    }

    private sealed class WriteHandle(RecordingFileSystem fs, string path, IWriteHandle inner) : IWriteHandle
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
        {
            if (fs.OnWrite?.Invoke(path, offset) is { } ex)
            {
                return ValueTask.FromException(ex);
            }

            var data = fs.TransformWrite is { } transform ? transform(path, offset, buffer) : buffer;
            return inner.WriteAsync(data, offset, ct);
        }

        public ValueTask SetLengthAsync(long length) => inner.SetLengthAsync(length);

        public ValueTask FlushAsync() => inner.FlushAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public string GetFinalPath() => inner.GetFinalPath();

        public void DeleteOnClose() => inner.DeleteOnClose();
    }
}
