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
        Assert.Empty(b.PartFiles());
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
        b.AddFile(src, data);
        IReadOnlyList<string>? partsDuring = null;
        bool? destDuring = null;
        b.Faults.MutateDuringRead(src, 512 * KiB, () =>
        {
            partsDuring = b.PartFiles();
            destDuring = b.Exists(dst);
        });

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = 64 * KiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var partDuring = Assert.Single(partsDuring!);
        Assert.Matches(@"dst\.bin\.[0-9a-f]{8}\.ferrypart$", partDuring);
        Assert.Equal(Path.GetFileName(outcome.PartPath), Path.GetFileName(partDuring));
        Assert.False(destDuring);
        Assert.True(b.Exists(dst));
        Assert.Empty(b.PartFiles());
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

        Assert.Empty(b.PartFiles());
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
        Assert.Empty(b.PartFiles());
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
        Assert.Empty(b.PartFiles());
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
        b.Hooks.OnWrite = (path, offset) => IsPart(path) && offset >= 5 * MiB ? new FileSystemException(path, IoErrorKind.DiskFull, null) : null;

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.DiskFull, outcome.Problem?.Kind);
        Assert.Equal(CopyMessages.NotEnoughSpace(b.DriveName), outcome.Problem?.Message);
        Assert.Equal(src, outcome.Problem?.Path);
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
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
        Assert.Empty(b.PartFiles());
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
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_ExistingPartFiles_AreLeftUntouched(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(10 * MiB, seed: 14);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        var oldStyle = dst + ".ferrypart";
        var otherAttempt = dst + ".deadbeef.ferrypart";
        var oldContent = Bytes(11 * MiB, seed: 15);
        var otherContent = Bytes(3000, seed: 16);
        b.AddFile(oldStyle, oldContent);
        b.AddFile(otherAttempt, otherContent);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.True(data.AsSpan().SequenceEqual(b.ReadAll(dst)));
        Assert.True(oldContent.AsSpan().SequenceEqual(b.ReadAll(oldStyle)), "A part file this attempt did not create was changed.");
        Assert.Equal(otherContent, b.ReadAll(otherAttempt));
        Assert.Equal(2, b.PartFiles().Count);
        Assert.NotNull(outcome.PartPath);
        Assert.Matches(@"dst\.bin\.[0-9a-f]{8}\.ferrypart$", outcome.PartPath);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_ConcurrentToSameDestination_OneWins_NoPartsLeft(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (srcA, srcB, dst) = (b.PathOf("a.bin"), b.PathOf("b.bin"), b.PathOf("dst.bin"));
        var dataA = Bytes((8 * MiB) + 3, seed: 30);
        var dataB = Bytes((6 * MiB) + 5, seed: 31);
        b.AddFile(srcA, dataA);
        b.AddFile(srcB, dataB);
        var partsSeenAtMove = 0;

        // The barrier's post-phase action runs once both copies have arrived and before either is released, so the count cannot race a move.
        using var bothAtMove = new Barrier(2, _ => partsSeenAtMove = b.PartFiles().Count);
        b.Hooks.BeforeMove = (_, _) => bothAtMove.SignalAndWait(TimeSpan.FromSeconds(10));
        var copier = new FileCopier(b.Fs, pool);
        var options = new CopyOptions { BufferSizeBytes = 256 * KiB };

        var a = Task.Run(() => copier.CopyAsync(srcA, dst, options, new PauseGate(), new LatestProgress(), Ct), Ct);
        var c = Task.Run(() => copier.CopyAsync(srcB, dst, options, new PauseGate(), new LatestProgress(), Ct), Ct);
        var outcomes = await Task.WhenAll(a, c);

        Assert.Equal(2, partsSeenAtMove);
        Assert.NotEqual(outcomes[0].PartPath, outcomes[1].PartPath);
        Assert.Single(outcomes, o => o.Success);
        var loser = Assert.Single(outcomes, o => !o.Success);
        Assert.Equal(IoErrorKind.AlreadyExists, loser.Problem?.Kind);
        var winnerData = outcomes[0].Success ? dataA : dataB;
        Assert.True(winnerData.AsSpan().SequenceEqual(b.ReadAll(dst)), "Destination does not match the winning source.");
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_WriterFailsWhileReaderMidRead_ReportsWritersError(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(4 * MiB, seed: 32));
        b.Hooks.BlockRead = (path, offset) => offset >= MiB; // the reader sits in a read until it is cancelled, then fails like an aborted I/O
        b.Hooks.OnWrite = (path, offset) => IsPart(path) ? new FileSystemException(path, IoErrorKind.DiskFull, null) : null;

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.DiskFull, outcome.Problem?.Kind);
        Assert.Equal(CopyMessages.NotEnoughSpace(b.DriveName), outcome.Problem?.Message);
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_ProgressCallbackThrows_AfterRename_StillSucceeds(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = Bytes(1000, seed: 33);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new ThrowingProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(data, b.ReadAll(dst));
        Assert.Empty(b.PartFiles());
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_PartFileVanishesBeforeRename_ReportsPlainProblem(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, Bytes(100_000, seed: 34));
        b.Hooks.BeforeMove = (from, _) => b.Faults.Delete(from);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.NotFound, outcome.Problem?.Kind);
        Assert.Equal("The temporary copy disappeared before it could be finished", outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
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

    internal static bool IsPart(string path) => path.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase);

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

/// <summary>Part-file naming and hooks (ruling R29).</summary>
public sealed class FileCopierPartNameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Backends() => new() { Backend.InMemory, Backend.RealFiles };

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Copy_250CharDestinationName_PartNameFitsAndStaysInFolder(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var src = b.PathOf("src.bin");
        var dst = b.PathOf(new string('n', 246) + ".bin"); // 250 characters
        b.AddFile(src, [1, 2, 3]);
        var parts = new List<string>();

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(
            src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), new FileCopyHooks { PartChosen = p => { parts.Add(p); return true; } }, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var part = Assert.Single(parts);
        Assert.True(Path.GetFileName(part).Length <= 255, $"{Path.GetFileName(part).Length} characters");
        Assert.EndsWith(FileCopier.PartSuffix, part, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(dst), Path.GetDirectoryName(part));
        Assert.Equal([1, 2, 3], b.ReadAll(dst));
    }

    [Fact]
    public void NewPartPath_ShortName_KeepsWholeName_AndNeverSplitsSurrogatePairs()
    {
        var shortPart = FileCopier.NewPartPath(@"C:\d\a.txt");
        Assert.Matches(@"^C:\\d\\a\.txt\.[0-9a-f]{8}\.ferrypart$", shortPart);

        var emoji = "\U0001F600"; // two chars
        var name = new string('x', 235) + emoji + new string('y', 10);
        var part = FileCopier.NewPartPath(@"C:\d\" + name);
        var partName = Path.GetFileName(part);
        Assert.True(partName.Length <= 255);
        Assert.False(char.IsHighSurrogate(partName[partName.IndexOf('.', StringComparison.Ordinal) - 1]));
    }

    [Fact]
    public async Task Copy_PartChosenRefuses_NothingIsCreated_AndTheCopyFails()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var src = b.PathOf("src.bin");
        var dst = b.PathOf("dst.bin");
        b.AddFile(src, [1, 2, 3]);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(
            src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), new FileCopyHooks { PartChosen = _ => false }, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(CopyMessages.NotRecorded, outcome.Problem!.Message);
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
    }

    [Fact]
    public async Task Copy_PartChosen_IsCalledBeforeThePartExists()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var src = b.PathOf("src.bin");
        b.AddFile(src, [1, 2, 3]);
        var existedWhenAnnounced = true;

        await new FileCopier(b.Fs, pool).CopyAsync(
            src, b.PathOf("dst.bin"), new CopyOptions(), new PauseGate(), new LatestProgress(),
            new FileCopyHooks { PartChosen = p => { existedWhenAnnounced = b.Exists(p); return true; } }, Ct);

        Assert.False(existedWhenAnnounced);
    }

    [Fact]
    public async Task Copy_BeforeCommitRefuses_PartDeleted_DestinationNeverAppears()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var src = b.PathOf("src.bin");
        var dst = b.PathOf("dst.bin");
        b.AddFile(src, [1, 2, 3]);
        var refusal = new Problem(src, IoErrorKind.Other, CopyMessages.VerifyMismatch);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(
            src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(),
            new FileCopyHooks { BeforeCommit = (_, _, _) => ValueTask.FromResult<Problem?>(refusal) }, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(refusal, outcome.Problem);
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
    }

    [Fact]
    public async Task Copy_ReplaceExisting_ReadOnlyOldFile_Replaced()
    {
        using var b = Backend.Create(Backend.RealFiles);
        using var pool = new BufferPool();
        var src = b.PathOf("src.bin");
        var dst = b.PathOf("dst.bin");
        b.AddFile(src, [9, 9]);
        b.AddFile(dst, [1]);
        File.SetAttributes(dst, FileAttributes.ReadOnly);

        var outcome = await new FileCopier(b.Fs, pool).CopyAsync(
            src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), new FileCopyHooks { ReplaceExisting = true }, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal([9, 9], b.ReadAll(dst));
        Assert.Empty(b.PartFiles());
    }
}

/// <summary>Records the latest progress report (thread safe).</summary>
internal sealed class LatestProgress : IProgress<FileProgress>
{
    private FileProgress? _last;

    public FileProgress? Last => Volatile.Read(ref _last);

    public void Report(FileProgress value) => Volatile.Write(ref _last, value);
}

/// <summary>A progress callback that always fails.</summary>
internal sealed class ThrowingProgress : IProgress<FileProgress>
{
    public void Report(FileProgress value) => throw new InvalidOperationException("Progress callback failed.");
}

/// <summary>
/// Decorator with test hooks for things <see cref="FaultInjectingFileSystem"/> cannot express: failing writes to files whose name is not
/// known in advance, a read that hangs until cancelled (then fails like an aborted I/O), and a hook just before a move.
/// </summary>
internal sealed class HookFileSystem(IFileSystem inner) : IFileSystem
{
    /// <summary>Returns an exception to throw for a write to (path, offset), or null to let it through.</summary>
    public Func<string, long, Exception?>? OnWrite { get; set; }

    /// <summary>True for a read at (path, offset) that should wait for cancellation and then fail.</summary>
    public Func<string, long, bool>? BlockRead { get; set; }

    public Action<string, string>? BeforeMove { get; set; }

    public IReadHandle OpenRead(string path, bool unbuffered) => new ReadHandle(this, path, inner.OpenRead(path, unbuffered));

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered) =>
        new WriteHandle(this, path, inner.CreateWrite(path, preallocate, unbuffered));

    public FileEntry GetEntry(string path) => inner.GetEntry(path);

    public FileEntry GetEntryFollowingLinks(string path) => inner.GetEntryFollowingLinks(path);

    public IEnumerable<FileEntry> EnumerateChildren(string directory) => inner.EnumerateChildren(directory);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string destination, bool overwrite)
    {
        BeforeMove?.Invoke(source, destination);
        inner.Move(source, destination, overwrite);
    }

    public void Delete(string path) => inner.Delete(path);

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc) =>
        inner.SetMetadata(path, attributes, creationUtc, lastWriteUtc);

    public long GetFreeSpace(string path) => inner.GetFreeSpace(path);

    public int GetSectorSize(string path) => inner.GetSectorSize(path);

    public SecurityCopyResult CopySecurity(string source, string target) => inner.CopySecurity(source, target);

    public void CopyAlternateStreams(string source, string target, bool allStreams, CancellationToken ct) =>
        inner.CopyAlternateStreams(source, target, allStreams, ct);

    public string GetFinalPath(string path) => inner.GetFinalPath(path);

    public string? GetVolumeId(string path) => inner.GetVolumeId(path);

    public bool DeleteVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, long expectedLength, DateTime expectedLastWriteUtc, string? mustBeUnder) =>
        inner.DeleteVerified(path, expectedFileId, expectedVolumeSerial, expectedLength, expectedLastWriteUtc, mustBeUnder);

    public bool DeleteFileContained(string path, string mustBeUnder) => inner.DeleteFileContained(path, mustBeUnder);

    public bool IsContained(string path, string root) => inner.IsContained(path, root);

    public bool DeleteEmptyFolderVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, string? mustBeUnder) =>
        inner.DeleteEmptyFolderVerified(path, expectedFileId, expectedVolumeSerial, mustBeUnder);

    public bool TryCloneBlocks(string source, string target) => inner.TryCloneBlocks(source, target);

    private sealed class ReadHandle(HookFileSystem fs, string path, IReadHandle inner) : IReadHandle
    {
        public long Length => inner.Length;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct) =>
            fs.BlockRead?.Invoke(path, offset) == true ? BlockAsync(ct) : inner.ReadAsync(buffer, offset, ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async ValueTask<int> BlockAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException ex)
            {
                throw new FileSystemException(path, IoErrorKind.Other, ex); // like ERROR_OPERATION_ABORTED surfacing as an I/O error
            }

            return 0;
        }
    }

    private sealed class WriteHandle(HookFileSystem fs, string path, IWriteHandle inner) : IWriteHandle
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct) =>
            fs.OnWrite?.Invoke(path, offset) is { } ex ? ValueTask.FromException(ex) : inner.WriteAsync(buffer, offset, ct);

        public ValueTask SetLengthAsync(long length) => inner.SetLengthAsync(length);

        public ValueTask FlushAsync() => inner.FlushAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public string GetFinalPath() => inner.GetFinalPath();

        public void DeleteOnClose() => inner.DeleteOnClose();
    }
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
        Hooks = new HookFileSystem(Faults);
    }

    public FaultInjectingFileSystem Faults { get; }

    public HookFileSystem Hooks { get; }

    public IFileSystem Fs => Hooks;

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

    /// <summary>Every <c>*.ferrypart</c> file in the test folder.</summary>
    public IReadOnlyList<string> PartFiles() =>
        Fs.EnumerateChildren(_root).Select(e => e.FullPath).Where(FileCopierTests.IsPart).ToList();

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
