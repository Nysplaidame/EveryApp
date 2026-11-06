using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Tests.Fakes;

namespace Ferry.Engine.Tests;

public sealed class FileCopierTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;

    public static TheoryData<string> Backends() => new() { Backend.InMemory, Backend.RealFiles };

    public static TheoryData<string, int> BackendsAndSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var backend in new[] { Backend.InMemory, Backend.RealFiles })
        {
            foreach (var size in new[] { 0, 1, 4095, 4096, 4097, MiB, (64 * MiB) + 1 })
            {
                data.Add(backend, size);
            }
        }

        return data;
    }

    /// <summary>Sizes around the unbuffered threshold, with tails that are not whole sectors.</summary>
    public static TheoryData<string, int> BackendsAndLargeSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var backend in new[] { Backend.InMemory, Backend.RealFiles })
        {
            foreach (var size in new[] { (8 * MiB) - 1, 8 * MiB, (8 * MiB) + 1, (8 * MiB) + 4095, (12 * MiB) + 513 })
            {
                data.Add(backend, size);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BackendsAndSizes))]
    public async Task Copy_IsByteIdentical(string backend, int size)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(size, seed: size);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Null(outcome.Problem);
        Assert.Equal(size, b.Fs.GetEntry(dst).Length);
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)), "Destination content differs from the source.");
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(BackendsAndLargeSizes))]
    public async Task Copy_LargeFileTails_AreExact(string backend, int size)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(size, seed: size);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 3 * MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(size, b.Fs.GetEntry(dst).Length);
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)), "Destination content differs from the source.");
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_WritesToPartFileThenRenames(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(MiB, seed: 1);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        var part = dst + FileCopier.PartSuffix;
        b.AddFile(src, data);
        bool? partDuring = null;
        bool? destDuring = null;
        b.Faults.MutateDuringRead(src, 512 * KiB, () =>
        {
            partDuring = b.Exists(part);
            destDuring = b.Exists(dst);
        });

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 64 * KiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.True(partDuring);
        Assert.False(destDuring);
        Assert.True(b.Exists(dst));
        Assert.False(b.Exists(part));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Cancel_RemovesPartFile(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(16 * MiB, seed: 2);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        b.Faults.MutateDuringRead(src, 8 * MiB, cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), cts.Token));

        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.False(b.Exists(dst));
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Pause_StopsProgressUntilResume()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        const long Budget = 1L << 40;
        b.Faults.SetFreeSpace(@"C:\", Budget); // lets the test count bytes written
        long Written() => Budget - b.Fs.GetFreeSpace(@"C:\");
        var data = Bytes(4 * MiB, seed: 3);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        var pause = new PauseGate();
        var progress = new LatestProgress();
        b.Faults.MutateDuringRead(src, MiB, pause.Pause);

        var copy = new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 64 * KiB }, pause, progress, Ct);
        await Task.Delay(300, Ct);
        var writtenAtPause = Written();
        var reportedAtPause = progress.Last?.Done;
        await Task.Delay(300, Ct);

        Assert.False(copy.IsCompleted);
        Assert.Equal(writtenAtPause, Written());
        Assert.Equal(reportedAtPause, progress.Last?.Done);
        Assert.Equal(writtenAtPause, reportedAtPause); // the writer reports where it stopped
        Assert.True(writtenAtPause < data.Length);
        Assert.False(b.Exists(dst));

        pause.Resume();
        var outcome = await copy;

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(data.Length, progress.Last?.Done);
        Assert.Equal(data.Length, progress.Last?.Total);
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_PreservesTimesAndAttributes(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(10_000, seed: 4));
        var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var written = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        b.Fs.SetMetadata(src, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive, created, written);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Null(outcome.Problem);
        var entry = b.Fs.GetEntry(dst);
        Assert.Equal(created, entry.CreationTimeUtc);
        Assert.Equal(written, entry.LastWriteTimeUtc);
        Assert.True(entry.Attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(entry.Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_ReturnsSourceHash(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes((9 * MiB) + 7, seed: 5);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        var copier = new FileCopier(b.Fs, pool);

        var sha = await copier.CopyAsync(src, dst, new CopyOptions { HashAlgorithm = HashAlgorithmKind.Sha256, BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);
        var xx = await copier.CopyAsync(src, dst + "2", new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.Equal(SHA256.HashData(data), sha.SourceHash);
        using var hasher = new StreamingHasher(HashAlgorithmKind.XxHash3);
        hasher.Append(data);
        Assert.Equal(hasher.Finish(), xx.SourceHash);
    }

    [Fact]
    public async Task Copy_SourceModifiedDuringCopy_ReportsProblem()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = Bytes(MiB, seed: 6);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.MutateDuringRead(src, 512 * KiB, () =>
        {
            b.Memory!.Clock = () => new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            b.Memory.AddFile(src, Bytes(MiB, seed: 99)); // same size, new content and time
        });

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 64 * KiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal("The file changed while it was being copied", outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Copy_SourceShrinksDuringCopy_ReportsProblem()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(MiB, seed: 7));
        b.Faults.MutateDuringRead(src, 0, () => b.Memory!.AddFile(src, Bytes(100, seed: 8)));

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 64 * KiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(CopyMessages.SourceChanged, outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
    }

    [Fact]
    public async Task SpeedLimit_IsRespected()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = Bytes(10 * MiB, seed: 9);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        var time = new AutoAdvancingTime();
        var start = time.GetTimestamp();

        var outcome = await new FileCopier(b.Fs, pool, time).CopyAsync(src, dst, new CopyOptions { SpeedLimitBytesPerSecond = 5 * MiB }, new PauseGate(), new LatestProgress(), Ct);

        var elapsed = time.GetElapsedTime(start);
        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.True(elapsed >= TimeSpan.FromSeconds(1.8), $"Took {elapsed} on the fake clock.");
        Assert.True(elapsed <= TimeSpan.FromSeconds(2.5), $"Took {elapsed} on the fake clock.");
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_WriteFails_ReportsProblem_AndLeavesNothing(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(16 * MiB, seed: 10));
        b.Faults.FailWrite(dst + FileCopier.PartSuffix, 5 * MiB, IoErrorKind.DiskFull);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.DiskFull, outcome.Problem?.Kind);
        Assert.Equal(CopyMessages.NotEnoughSpace(b.DriveName), outcome.Problem?.Message);
        Assert.Equal(src, outcome.Problem?.Path);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_ReadFails_ReportsProblem_AndLeavesNothing(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(16 * MiB, seed: 11));
        b.Faults.FailRead(src, 7 * MiB, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.BadBlock, outcome.Problem?.Kind);
        Assert.Equal("The disk couldn't read part of this file", outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_DestinationExists_ReportsAlreadyExists_AndKeepsIt(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(100_000, seed: 12));
        var existing = Bytes(5, seed: 13);
        b.AddFile(dst, existing);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.AlreadyExists, outcome.Problem?.Kind);
        Assert.Equal(existing, b.ReadAll(dst));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_StalePartFile_IsReplaced(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(10 * MiB, seed: 14);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.AddFile(dst + FileCopier.PartSuffix, Bytes(11 * MiB, seed: 15)); // left over from a crash, longer than the new copy

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)));
        Assert.False(b.Exists(dst + FileCopier.PartSuffix));
    }

    [Fact]
    public async Task Copy_WithoutKeepDates_LeavesNewTimes()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(1000, seed: 16));
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        b.Fs.SetMetadata(src, FileAttributes.Hidden, old, old);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { KeepDatesAndAttributes = false, Verify = false }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success);
        Assert.Null(outcome.SourceHash);
        var entry = b.Fs.GetEntry(dst);
        Assert.NotEqual(old, entry.LastWriteTimeUtc);
        Assert.False(entry.Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void BufferPool_CountsRentsAndReturns()
    {
        using var pool = new BufferPool();
        var a = pool.Rent(4096);
        var c = pool.Rent(MiB);

        Assert.Equal(2, pool.Outstanding);
        pool.Return(a);
        pool.Return(c);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(2, pool.RentCount);
        Assert.Equal(2, pool.ReturnCount);
        Assert.True(c.Size >= MiB);
    }

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}

/// <summary>Runs alone so process-wide allocation counts are not disturbed by other tests.</summary>
[CollectionDefinition(nameof(RunsAlone), DisableParallelization = true)]
public sealed class RunsAlone;

[Collection(nameof(RunsAlone))]
public sealed class FileCopierAllocationTests
{
    [Fact]
    public async Task Copy_SteadyState_AllocatesNothingPerChunk()
    {
        // The copy loops run on pool threads, so this measures the whole process (the collection runs alone) rather than one thread.
        // Two 64 MiB copies differ only in chunk count (1024 vs 16); the in-memory fake's own storage is the same for both and cancels out.
        var engine = typeof(FileCopier).Assembly.GetCustomAttribute<DebuggableAttribute>();
        Assert.SkipWhen(engine?.IsJITOptimizerDisabled == true, "Unoptimised (Debug) builds allocate every async state machine; build.ps1 measures Release.");
        var fs = new InMemoryFileSystem { SectorSize = 4096 };
        var data = FileCopierTests.Bytes(64 * 1024 * 1024, seed: 20);
        fs.AddFile(@"C:\a\src.bin", data);
        using var pool = new BufferPool();
        var copier = new FileCopier(fs, pool);
        var progress = new LatestProgress();
        var run = 0;

        async Task<long> MeasureAsync(int bufferSize)
        {
            var dst = $@"C:\a\dst{run++}.bin";
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var outcome = await copier.CopyAsync(@"C:\a\src.bin", dst, new CopyOptions { BufferSizeBytes = bufferSize }, new PauseGate(), progress, FileCopierTests.Ct);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Assert.True(outcome.Success);
            fs.Delete(dst);
            return allocated;
        }

        await MeasureAsync(64 * 1024); // warm up JIT and the pool
        await MeasureAsync(4 * 1024 * 1024);
        var manyChunks = await MeasureAsync(64 * 1024);
        var fewChunks = await MeasureAsync(4 * 1024 * 1024);
        var perChunk = (manyChunks - fewChunks) / (1024.0 - 16);

        TestContext.Current.SendDiagnosticMessage($"64 KiB chunks: {manyChunks:N0} B, 4 MiB chunks: {fewChunks:N0} B, about {perChunk:N1} B per chunk");
        Assert.True(perChunk < 64, $"About {perChunk:N1} bytes allocated per chunk ({manyChunks:N0} vs {fewChunks:N0}).");
    }
}

/// <summary>Records the latest progress report (thread safe).</summary>
internal sealed class LatestProgress : IProgress<FileProgress>
{
    private FileProgress? _last;

    public FileProgress? Last => Volatile.Read(ref _last);

    public void Report(FileProgress value) => Volatile.Write(ref _last, value);
}

/// <summary>Fake clock whose timers fire at once and move the clock forward by their due time, so delays cost no real time.</summary>
internal sealed class AutoAdvancingTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Interlocked.Add(ref _ticks, Math.Max(0, dueTime.Ticks));
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The same test against the in-memory fake or real temp files, both behind a <see cref="FaultInjectingFileSystem"/>.</summary>
internal sealed class Backend : IDisposable
{
    public const string InMemory = "memory";
    public const string RealFiles = "real";

    private readonly string _root;

    private Backend(string root, IFileSystem inner, InMemoryFileSystem? memory)
    {
        _root = root;
        Memory = memory;
        Faults = new FaultInjectingFileSystem(inner);
    }

    public FaultInjectingFileSystem Faults { get; }

    public IFileSystem Fs => Faults;

    /// <summary>The in-memory fake, or null for real files.</summary>
    public InMemoryFileSystem? Memory { get; }

    /// <summary>Drive name as messages show it, e.g. "C:".</summary>
    public string DriveName => Path.GetPathRoot(_root)!.TrimEnd('\\');

    public static Backend Create(string kind)
    {
        if (kind == RealFiles)
        {
            var root = Path.Combine(Path.GetTempPath(), "ferry-copier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Backend(root, new WindowsFileSystem(), null);
        }

        var fs = new InMemoryFileSystem { SectorSize = 4096 };
        fs.CreateDirectory(@"C:\work");
        return new Backend(@"C:\work", fs, fs);
    }

    public string PathOf(string name) => Path.Combine(_root, name);

    public void AddFile(string path, byte[] data)
    {
        if (Memory is not null)
        {
            Memory.AddFile(path, data);
        }
        else
        {
            File.WriteAllBytes(path, data);
        }
    }

    public byte[] ReadAll(string path) => Memory is not null ? Memory.ReadAllBytes(path) : File.ReadAllBytes(path);

    public bool Exists(string path) => Memory is not null ? Memory.Exists(path) : File.Exists(path);

    public void Dispose()
    {
        if (Memory is not null || !Directory.Exists(_root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_root))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
