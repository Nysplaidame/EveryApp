using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Tests.Fakes;

namespace Ferry.Engine.Tests;

/// <summary>Rescue mode: saving every readable byte of a file on a failing drive, and saying exactly what was lost.</summary>
public sealed class RescueReaderTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CopyOptions Rescue = new() { RescueMode = true };

    [Fact]
    public async Task Rescue_BadSectorInMiddle_RecoversRestAndReportsRange()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes(MiB, seed: 1);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailReadRange(src, 8192, 4096, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal([new LostRange(8192, 4096)], outcome.Lost);
        var expected = (byte[])data.Clone();
        Array.Clear(expected, 8192, 4096);
        Assert.Equal(expected, b.ReadAll(dst));
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Rescue_RescuedFile_IsAWarningNotACleanSuccess_AndHashCoversWhatWasWritten()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes(MiB, seed: 2);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailReadRange(src, 8192, 4096, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success);
        Assert.Equal(IoErrorKind.BadBlock, outcome.Problem?.Kind);
        Assert.Equal(
            "Part of this file couldn't be read and was filled with blanks (4 KB lost) — see dst.bin.ferry-lost.txt",
            outcome.Problem?.Message);
        var expected = (byte[])data.Clone();
        Array.Clear(expected, 8192, 4096);
        using var hasher = new StreamingHasher(HashAlgorithmKind.XxHash3);
        hasher.Append(expected);
        Assert.Equal(hasher.Finish(), outcome.SourceHash);
    }

    [Fact]
    public async Task Rescue_Off_BadBlockFailsFile()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(MiB, seed: 3));
        b.Faults.FailReadRange(src, 8192, 4096, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.BadBlock, outcome.Problem?.Kind);
        Assert.Equal(CopyMessages.CouldNotRead, outcome.Problem?.Message);
        Assert.Empty(outcome.Lost);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + ".ferry-lost.txt"));
        Assert.Empty(b.PartFiles());
    }

    [Fact]
    public async Task Rescue_OtherErrorKinds_AreNotRescued()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(MiB, seed: 4));
        b.Faults.FailReadRange(src, 8192, 4096, IoErrorKind.AccessDenied);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.AccessDenied, outcome.Problem?.Kind);
        Assert.False(b.Exists(dst));
    }

    [Theory]
    [MemberData(nameof(FileCopierTests.Backends), MemberType = typeof(FileCopierTests))]
    public async Task Rescue_WritesLostReport(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(MiB, seed: 5));
        b.Faults.FailReadRange(src, 8192, 4096, IoErrorKind.BadBlock);
        b.Faults.FailReadRange(src, 100 * KiB, 4096, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var report = Encoding.UTF8.GetString(b.ReadAll(dst + ".ferry-lost.txt"));
        Assert.Contains("Bytes 8,192 to 12,287 (4 KB) couldn't be read and were filled with blanks.", report, StringComparison.Ordinal);
        Assert.Contains(PathDisplay.ToDisplayPath(src), report, StringComparison.Ordinal);
        Assert.Contains("1970-01-01", report, StringComparison.Ordinal); // the date, from the injected clock
        Assert.Contains("8 KB lost", outcome.Problem!.Message, StringComparison.Ordinal);
        Assert.Empty(b.PartFiles());
    }

    [Fact]
    public async Task Rescue_LostReportName_StaysWithin255Characters()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var name = new string('n', 250) + ".bin";
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf(name));
        b.AddFile(src, FileCopierTests.Bytes(64 * KiB, seed: 6));
        b.Faults.FailReadRange(src, 0, 4096, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var reports = b.Fs.EnumerateChildren(b.PathOf(string.Empty).TrimEnd('\\'))
            .Select(e => Path.GetFileName(e.FullPath))
            .Where(n => n.EndsWith(".ferry-lost.txt", StringComparison.Ordinal))
            .ToList();
        var report = Assert.Single(reports);
        Assert.True(report.Length <= 255, $"{report.Length} characters");
        Assert.Contains(report, outcome.Problem!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rescue_TooMuchLost_FailsFileWithPlainProblem()
    {
        using var b = Backend.Create(Backend.InMemory);
        b.Memory!.SectorSize = 512; // 4,096 sectors = 2 MiB
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(3 * MiB, seed: 7));
        b.Faults.FailReadRange(src, 0, 3 * MiB, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.True(outcome.NoRetry);
        Assert.Equal(IoErrorKind.BadBlock, outcome.Problem?.Kind);
        Assert.Equal("Too much of this file is unreadable to rescue", outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.False(b.Exists(dst + ".ferry-lost.txt"));
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task RescueReader_SectorThatFailsTwice_IsRecoveredOnItsLastRetry()
    {
        var mem = new InMemoryFileSystem { SectorSize = 4096 };
        var faults = new FaultInjectingFileSystem(mem);
        var data = FileCopierTests.Bytes(64 * KiB, seed: 8);
        mem.AddFile(@"C:\f.bin", data);
        faults.FailReadRange(@"C:\f.bin", 8192, 4096, IoErrorKind.BadBlock, times: 2);
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        var buffer = new byte[4096];

        var lost = await new RescueReader(faults, 4096).ReadChunkAsync(h, buffer, 8192, Ct);

        Assert.Empty(lost);
        Assert.Equal(data.AsSpan(8192, 4096).ToArray(), buffer);
    }

    [Fact]
    public async Task RescueReader_SectorThatFailsThreeTimes_IsZeroFilled()
    {
        var mem = new InMemoryFileSystem { SectorSize = 4096 };
        var faults = new FaultInjectingFileSystem(mem);
        mem.AddFile(@"C:\f.bin", FileCopierTests.Bytes(64 * KiB, seed: 9));
        faults.FailReadRange(@"C:\f.bin", 8192, 4096, IoErrorKind.BadBlock, times: 3);
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        var buffer = new byte[4096];
        buffer.AsSpan().Fill(0xAB);

        var lost = await new RescueReader(faults, 4096).ReadChunkAsync(h, buffer, 8192, Ct);

        Assert.Equal([new LostRange(8192, 4096)], lost);
        Assert.All(buffer, x => Assert.Equal(0, x));
    }

    [Fact]
    public async Task RescueReader_AdjacentBadSectors_AreMergedIntoOneRange()
    {
        var mem = new InMemoryFileSystem { SectorSize = 512 };
        var faults = new FaultInjectingFileSystem(mem);
        var data = FileCopierTests.Bytes(256 * KiB, seed: 10);
        mem.AddFile(@"C:\f.bin", data);
        faults.FailReadRange(@"C:\f.bin", 4096, 512, IoErrorKind.BadBlock);
        faults.FailReadRange(@"C:\f.bin", 4608, 1024, IoErrorKind.BadBlock);
        faults.FailReadRange(@"C:\f.bin", 65536, 512, IoErrorKind.BadBlock);
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        var buffer = new byte[128 * KiB];

        var lost = await new RescueReader(faults, 512).ReadChunkAsync(h, buffer, 0, Ct);

        Assert.Equal([new LostRange(4096, 1536), new LostRange(65536, 512)], lost);
        var expected = data[..(128 * KiB)];
        Array.Clear(expected, 4096, 1536);
        Array.Clear(expected, 65536, 512);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public async Task RescueReader_StopsAtEndOfFile()
    {
        var mem = new InMemoryFileSystem { SectorSize = 512 };
        var faults = new FaultInjectingFileSystem(mem);
        mem.AddFile(@"C:\f.bin", FileCopierTests.Bytes(5000, seed: 11));
        faults.FailReadRange(@"C:\f.bin", 4608, 392, IoErrorKind.BadBlock); // the last, partial sector
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        var buffer = new byte[8192];

        var lost = await new RescueReader(faults, 512).ReadChunkAsync(h, buffer, 0, Ct);

        Assert.Equal([new LostRange(4608, 392)], lost);
    }

    [Fact]
    public async Task RescueReader_WaitsWhilePaused()
    {
        var mem = new InMemoryFileSystem { SectorSize = 4096 };
        var faults = new FaultInjectingFileSystem(mem);
        mem.AddFile(@"C:\f.bin", FileCopierTests.Bytes(64 * KiB, seed: 12));
        faults.FailReadRange(@"C:\f.bin", 8192, 4096, IoErrorKind.BadBlock);
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        var pause = new PauseGate();
        pause.Pause();

        var task = new RescueReader(faults, 4096, pause).ReadChunkAsync(h, new byte[64 * KiB], 0, Ct);
        await Task.Delay(100, Ct);
        Assert.False(task.IsCompleted);

        pause.Resume();
        Assert.Equal([new LostRange(8192, 4096)], await task);
    }

    [Fact]
    public async Task RescueReader_Cancelled_Throws()
    {
        var mem = new InMemoryFileSystem { SectorSize = 4096 };
        var faults = new FaultInjectingFileSystem(mem);
        mem.AddFile(@"C:\f.bin", FileCopierTests.Bytes(64 * KiB, seed: 13));
        faults.FailReadRange(@"C:\f.bin", 8192, 4096, IoErrorKind.BadBlock);
        await using var h = faults.OpenRead(@"C:\f.bin", unbuffered: false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new RescueReader(faults, 4096).ReadChunkAsync(h, new byte[64 * KiB], 0, cts.Token));
    }

    [Fact]
    public async Task Rescue_LargeUnbufferedFile_RecoversAroundBadRange()
    {
        using var b = Backend.Create(Backend.RealFiles);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes((12 * MiB) + 513, seed: 14);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailReadRange(src, 9 * MiB, 8192, IoErrorKind.BadBlock);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, Rescue, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var lost = Assert.Single(outcome.Lost);
        Assert.True(lost.Offset <= 9 * MiB && lost.Offset + lost.Length >= (9 * MiB) + 8192, lost.ToString());
        var expected = (byte[])data.Clone();
        Array.Clear(expected, (int)lost.Offset, lost.Length);
        Assert.Equal(expected, b.ReadAll(dst));
    }
}
