using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

namespace Ferry.Engine.Tests;

/// <summary>Network retry: waiting out brief drops (with a fake clock, so no test really sleeps).</summary>
public sealed class RetryPolicyTests
{
    private const int MiB = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- RetryPolicy on its own ----

    [Fact]
    public async Task Retry_BacksOffExponentially_From1sUpTo30s()
    {
        var time = new AutoAdvancingTime();
        var calls = new List<DateTimeOffset>();
        var policy = new RetryPolicy(time: time);

        var result = await policy.RunAsync(
            () =>
            {
                calls.Add(time.GetUtcNow());
                return calls.Count <= 8 ? throw Lost() : Task.FromResult(42);
            },
            RetryPolicy.IsTransient,
            null,
            Ct);

        Assert.Equal(42, result);
        var gaps = calls.Zip(calls.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], gaps);
    }

    [Fact]
    public async Task Retry_GivesUpAfterMaxWait_FiveMinutesByDefault()
    {
        var time = new AutoAdvancingTime();
        var start = time.GetUtcNow();
        var calls = 0;

        var ex = await Assert.ThrowsAsync<RetryGaveUpException>(() => new RetryPolicy(time: time).RunAsync<int>(
            () =>
            {
                calls++;
                throw Lost();
            },
            RetryPolicy.IsTransient,
            null,
            Ct));

        Assert.Equal(TimeSpan.FromMinutes(5), ex.Waited);
        Assert.Equal(TimeSpan.FromMinutes(5), time.GetUtcNow() - start);
        Assert.Equal(IoErrorKind.NetworkLost, ex.Last.Kind);
        Assert.InRange(calls, 2, 30);
    }

    [Fact]
    public async Task Retry_GivesUpAfterMaxAttempts()
    {
        var time = new AutoAdvancingTime();
        var calls = 0;

        var ex = await Assert.ThrowsAsync<RetryGaveUpException>(() => new RetryPolicy(maxAttempts: 3, time: time).RunAsync<int>(
            () =>
            {
                calls++;
                throw Lost();
            },
            RetryPolicy.IsTransient,
            null,
            Ct));

        Assert.Equal(3, calls);
        Assert.Equal(TimeSpan.FromSeconds(3), ex.Waited);
    }

    [Theory]
    [InlineData(IoErrorKind.BadBlock)]
    [InlineData(IoErrorKind.AccessDenied)]
    [InlineData(IoErrorKind.DiskFull)]
    [InlineData(IoErrorKind.Other)]
    public async Task Retry_OtherErrors_FailAtOnce(IoErrorKind kind)
    {
        var time = new AutoAdvancingTime();
        var calls = 0;

        var ex = await Assert.ThrowsAsync<FileSystemException>(() => new RetryPolicy(time: time).RunAsync<int>(
            () =>
            {
                calls++;
                throw new FileSystemException("x", kind, null);
            },
            RetryPolicy.IsTransient,
            null,
            Ct));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(1, calls);
        Assert.Equal(0, time.GetTimestamp());
    }

    [Theory]
    [InlineData(IoErrorKind.NetworkLost, true)]
    [InlineData(IoErrorKind.SharingViolation, true)]
    [InlineData(IoErrorKind.BadBlock, false)]
    [InlineData(IoErrorKind.NotFound, false)]
    public void Retry_TransientKinds_AreNetworkLostAndSharingViolationOnly(IoErrorKind kind, bool transient) =>
        Assert.Equal(transient, RetryPolicy.IsTransient(kind));

    [Theory]
    [InlineData(IoErrorKind.NetworkLost, "Waiting for the network drive to come back…")]
    [InlineData(IoErrorKind.SharingViolation, "Waiting for another program to finish with this file…")]
    public async Task Retry_ShowsPlainStatusWhileWaiting(IoErrorKind kind, string expected)
    {
        var status = new StatusLog();
        var calls = 0;

        await new RetryPolicy(time: new AutoAdvancingTime()).RunAsync(
            () => ++calls == 1 ? throw new FileSystemException("x", kind, null) : Task.FromResult(true),
            RetryPolicy.IsTransient,
            status,
            Ct);

        Assert.Contains(expected, status.Messages);
    }

    [Fact]
    public async Task Retry_CancelledWhileWaiting_Throws()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RetryPolicy(time: TimeProvider.System).RunAsync<int>(
            () =>
            {
                cts.Cancel();
                throw Lost();
            },
            RetryPolicy.IsTransient,
            null,
            cts.Token));
    }

    // ---- In the copier ----

    [Theory]
    [MemberData(nameof(FileCopierTests.Backends), MemberType = typeof(FileCopierTests))]
    public async Task Copy_NetworkDropsThenReturns_ResumesAndCompletes(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes(4 * MiB, seed: 21);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailWriteWhere(FileCopierTests.IsPart, 2 * MiB, IoErrorKind.NetworkLost, times: 3);
        var status = new StatusLog();

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(
            src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), new FileCopyHooks { Status = status }, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Null(outcome.Problem);
        Assert.Equal(data, b.ReadAll(dst));
        Assert.Contains("Waiting for the network drive to come back…", status.Messages);
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    public static TheoryData<string, long> DropPoints()
    {
        var data = new TheoryData<string, long>();
        foreach (var backend in new[] { Backend.InMemory, Backend.RealFiles })
        {
            data.Add(backend, 5L * MiB); // a whole chunk in the middle
            data.Add(backend, 12L * MiB); // the short final chunk (padded and trimmed on unbuffered handles)
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DropPoints))]
    public async Task Copy_NetworkDropsMidWrite_HandleDies_ReopensPartAndOutputIsIdentical(string backend, long dropAt)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes((12 * MiB) + 513, seed: 22);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailWriteWhere(FileCopierTests.IsPart, dropAt, IoErrorKind.NetworkLost, times: 1, handleDies: true);

        // Verify off: the copier's own check after a reopen must still hold the output to the source.
        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(
            src, dst, new CopyOptions { BufferSizeBytes = MiB, Verify = false }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(data, b.ReadAll(dst));
        Assert.True(b.Faults.ReopenedWrites >= 1);
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Theory]
    [MemberData(nameof(FileCopierTests.Backends), MemberType = typeof(FileCopierTests))]
    public async Task Copy_NetworkDropsMidRead_HandleDies_ReopensSourceAndOutputIsIdentical(string backend)
    {
        using var b = Backend.Create(backend);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes((12 * MiB) + 513, seed: 23);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailReadRange(src, 5 * MiB, 1, IoErrorKind.NetworkLost, times: 1, handleDies: true);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(
            src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(data, b.ReadAll(dst));
        Assert.Equal(2, b.Faults.OpenedReads);
    }

    [Fact]
    public async Task Copy_DataLostByDeadHandle_IsCaughtEvenWithVerifyOff()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(4 * MiB, seed: 24));
        b.Faults.FailWriteWhere(FileCopierTests.IsPart, 2 * MiB, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        b.Faults.BeforeOpenWriteExisting = part =>
        {
            // Writes the dead handle had reported as done never reached the disk.
            var h = b.Memory!.OpenWriteExisting(part, unbuffered: false);
            h.WriteAsync(new byte[4096], 0, Ct).AsTask().GetAwaiter().GetResult();
            h.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(
            src, dst, new CopyOptions { BufferSizeBytes = MiB, Verify = false }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(CopyMessages.VerifyMismatch, outcome.Problem?.Message);
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
    }

    [Fact]
    public async Task Retry_GivesUpAfterMaxWait_ReportsProblem()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, FileCopierTests.Bytes(4 * MiB, seed: 25));
        b.Faults.FailWriteWhere(FileCopierTests.IsPart, 2 * MiB, IoErrorKind.NetworkLost);
        var time = new AutoAdvancingTime();

        var outcome = await new FileCopier(b.Fs, pool, time).CopyAsync(
            src, dst, new CopyOptions { BufferSizeBytes = MiB }, new PauseGate(), new LatestProgress(), Ct);

        Assert.False(outcome.Success);
        Assert.True(outcome.NoRetry);
        Assert.Equal(IoErrorKind.NetworkLost, outcome.Problem?.Kind);
        Assert.Equal("The network connection was lost, even after waiting 5 minutes", outcome.Problem?.Message);
        Assert.True(time.GetUtcNow() - DateTimeOffset.UnixEpoch >= TimeSpan.FromMinutes(5));
        Assert.False(b.Exists(dst));
        Assert.Empty(b.PartFiles());
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Copy_SourceBrieflyInUse_WaitsAndCompletes()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes(MiB, seed: 26);
        var (src, dst) = (b.PathOf("src.bin"), b.PathOf("dst.bin"));
        b.AddFile(src, data);
        b.Faults.FailRead(src, 0, IoErrorKind.SharingViolation, times: 2);

        var outcome = await new FileCopier(b.Fs, pool, new AutoAdvancingTime()).CopyAsync(src, dst, new CopyOptions(), new PauseGate(), new LatestProgress(), Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(data, b.ReadAll(dst));
    }

    private static FileSystemException Lost() => new("x", IoErrorKind.NetworkLost, null);

    private sealed class StatusLog : IProgress<string>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public void Report(string value)
        {
            lock (_messages)
            {
                _messages.Add(value);
            }
        }
    }
}
