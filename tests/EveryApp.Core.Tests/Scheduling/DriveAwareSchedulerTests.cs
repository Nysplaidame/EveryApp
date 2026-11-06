using EveryApp.Core.Scheduling;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Scheduling;

public sealed class DriveAwareSchedulerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameSourceDisk_SecondJobWaits()
    {
        var resolver = new FakeDeviceResolver().Map("src1", "disk:1").Map("dst1", "disk:2").Map("src2", "disk:1").Map("dst2", "disk:3");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var first = await scheduler.AcquireAsync("src1", "dst1", Ct);
        var second = scheduler.AcquireAsync("src2", "dst2", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Short, Ct));
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task DifferentDisks_RunInParallel()
    {
        var resolver = new FakeDeviceResolver().Map("src1", "disk:1").Map("dst1", "disk:2").Map("src2", "disk:3").Map("dst2", "disk:4");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var first = await scheduler.AcquireAsync("src1", "dst1", Ct);
        await using var second = await scheduler.AcquireAsync("src2", "dst2", Ct).WaitAsync(Long, Ct);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task SameSmbServer_Serialized()
    {
        var resolver = new FakeDeviceResolver().Map("a", "smb:nas").Map("b", "disk:1").Map("c", "smb:nas").Map("d", "disk:2");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var first = await scheduler.AcquireAsync("a", "b", Ct);
        var second = scheduler.AcquireAsync("c", "d", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Short, Ct));
    }

    [Fact]
    public async Task StorageSpace_MapsToAllMemberDisks()
    {
        // The virtual disk resolves to its own number plus every member disk, so a job on a plain disk that is a member waits.
        var resolver = new FakeDeviceResolver().Map("pool", "disk:9", "disk:6", "disk:7").Map("member", "disk:6").Map("elsewhere", "disk:1").Map("other", "disk:2");
        var scheduler = new DriveAwareScheduler(resolver);

        Assert.True(resolver.Resolve("pool").Overlaps(resolver.Resolve("member")));

        await using var first = await scheduler.AcquireAsync("pool", "elsewhere", Ct);
        var second = scheduler.AcquireAsync("member", "other", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Short, Ct));
    }

    [Fact]
    public async Task ReleaseOnDispose_StartsWaitingJob()
    {
        var resolver = new FakeDeviceResolver().Map("a", "disk:1").Map("b", "disk:2").Map("c", "disk:1").Map("d", "disk:3");
        var scheduler = new DriveAwareScheduler(resolver);

        var first = await scheduler.AcquireAsync("a", "b", Ct);
        var second = scheduler.AcquireAsync("c", "d", Ct);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();

        await using var lease = await second.WaitAsync(Long, Ct);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task WaitingReason_IsPlainLanguage()
    {
        var resolver = new FakeDeviceResolver().Map("a", "disk:1").Map("b", "disk:2").Map("c", "disk:1").Map("d", "disk:3");
        var scheduler = new DriveAwareScheduler(resolver);
        var raised = new List<(string Label, string Reason)>();
        scheduler.Waiting += (label, reason) => raised.Add((label, reason));

        await using var first = await scheduler.AcquireAsync("a", "b", Ct);
        Assert.Empty(raised); // no wait, no event
        var second = scheduler.AcquireAsync("c", "d", Ct);

        var (_, reason) = Assert.Single(raised);
        Assert.Equal("Waiting: uses the same drive as another copy", reason);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public async Task Fifo_LaterJobCannotJumpAnEarlierWaiterItOverlaps()
    {
        // active holds disk:1. waiter W needs disk:1 and disk:2. A later job L needs only disk:2: free right now,
        // but W is ahead of it and overlaps it, so L must not start (otherwise L-like jobs could starve W forever).
        var resolver = new FakeDeviceResolver()
            .Map("active", "disk:1").Map("activeDst", "disk:5")
            .Map("w", "disk:1", "disk:2").Map("wDst", "disk:6")
            .Map("l", "disk:2").Map("lDst", "disk:7");
        var scheduler = new DriveAwareScheduler(resolver);

        var active = await scheduler.AcquireAsync("active", "activeDst", Ct);
        var waiter = scheduler.AcquireAsync("w", "wDst", Ct);
        var later = scheduler.AcquireAsync("l", "lDst", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => later.WaitAsync(Short, Ct));
        Assert.False(waiter.IsCompleted);

        await active.DisposeAsync();
        var waiterLease = await waiter.WaitAsync(Long, Ct);
        Assert.False(later.IsCompleted); // still blocked by the now-active W

        await waiterLease.DisposeAsync();
        await using var laterLease = await later.WaitAsync(Long, Ct);
        Assert.NotNull(laterLease);
    }

    [Fact]
    public async Task Fifo_LaterNonOverlappingJobStartsWhileAnotherWaits()
    {
        var resolver = new FakeDeviceResolver()
            .Map("active", "disk:1").Map("activeDst", "disk:5")
            .Map("w", "disk:1").Map("wDst", "disk:6")
            .Map("free", "disk:8").Map("freeDst", "disk:9");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var active = await scheduler.AcquireAsync("active", "activeDst", Ct);
        var waiter = scheduler.AcquireAsync("w", "wDst", Ct);
        await using var free = await scheduler.AcquireAsync("free", "freeDst", Ct).WaitAsync(Long, Ct);

        Assert.False(waiter.IsCompleted);
    }

    [Fact]
    public async Task CancelWhileWaiting_RemovesJobFromQueue()
    {
        var resolver = new FakeDeviceResolver()
            .Map("a", "disk:1").Map("aDst", "disk:5")
            .Map("b", "disk:1").Map("bDst", "disk:6")
            .Map("c", "disk:1").Map("cDst", "disk:7");
        var scheduler = new DriveAwareScheduler(resolver);
        var waitingCount = 0;
        scheduler.Waiting += (_, _) => Interlocked.Increment(ref waitingCount);

        var first = await scheduler.AcquireAsync("a", "aDst", Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = scheduler.AcquireAsync("b", "bDst", cts.Token);
        var third = scheduler.AcquireAsync("c", "cDst", Ct);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The cancelled job must not hold the line: when the first job ends, the third starts.
        await first.DisposeAsync();
        await using var thirdLease = await third.WaitAsync(Long, Ct);
        Assert.NotNull(thirdLease);
        Assert.Equal(2, waitingCount);
    }

    [Fact]
    public async Task CancelWhileWaiting_UnblocksJobBehindIt()
    {
        // B (waiting, disk:1+2) is cancelled; C (disk:2) was blocked only by B and must start at once.
        var resolver = new FakeDeviceResolver()
            .Map("a", "disk:1").Map("aDst", "disk:5")
            .Map("b", "disk:1", "disk:2").Map("bDst", "disk:6")
            .Map("c", "disk:2").Map("cDst", "disk:7");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var first = await scheduler.AcquireAsync("a", "aDst", Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var b = scheduler.AcquireAsync("b", "bDst", cts.Token);
        var c = scheduler.AcquireAsync("c", "cDst", Ct);
        Assert.False(c.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);

        await using var cLease = await c.WaitAsync(Long, Ct);
        Assert.NotNull(cLease);
    }

    [Fact]
    public async Task AlreadyCancelledToken_Throws()
    {
        var scheduler = new DriveAwareScheduler(new FakeDeviceResolver());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.AcquireAsync("a", "b", cts.Token));
    }

    [Fact]
    public async Task DisposingHandleTwice_IsHarmless()
    {
        var resolver = new FakeDeviceResolver().Map("a", "disk:1").Map("b", "disk:2").Map("c", "disk:1").Map("d", "disk:3").Map("e", "disk:1").Map("f", "disk:4");
        var scheduler = new DriveAwareScheduler(resolver);

        var first = await scheduler.AcquireAsync("a", "b", Ct);
        var second = scheduler.AcquireAsync("c", "d", Ct);
        var third = scheduler.AcquireAsync("e", "f", Ct);

        await first.DisposeAsync();
        var secondLease = await second.WaitAsync(Long, Ct);
        await first.DisposeAsync(); // must not release the second job's slot or start the third

        await Assert.ThrowsAsync<TimeoutException>(() => third.WaitAsync(Short, Ct));
        await secondLease.DisposeAsync();
        await using var thirdLease = await third.WaitAsync(Long, Ct);
        Assert.NotNull(thirdLease);
    }

    [Fact]
    public async Task SameDiskForSourceAndDestination_StillRunsAlone()
    {
        // A copy within one disk is one job that uses the disk once; it must not deadlock against itself.
        var resolver = new FakeDeviceResolver().Map("a", "disk:1").Map("b", "disk:1");
        var scheduler = new DriveAwareScheduler(resolver);

        await using var lease = await scheduler.AcquireAsync("a", "b", Ct).WaitAsync(Long, Ct);
        Assert.NotNull(lease);
    }

    private sealed class IdListResolver : IDeviceResolver
    {
        public DeviceSet Resolve(string path) => DeviceSet.Of(path.Split(','));
    }

    [Fact]
    public async Task OneRelease_AdmitsTwoNonOverlappingWaiters()
    {
        var scheduler = new DriveAwareScheduler(new IdListResolver());

        var first = await scheduler.AcquireAsync("disk:1,disk:2", "disk:9", Ct);
        var b = scheduler.AcquireAsync("disk:1", "disk:10", Ct);
        var c = scheduler.AcquireAsync("disk:2", "disk:11", Ct);
        Assert.False(b.IsCompleted);
        Assert.False(c.IsCompleted);

        await first.DisposeAsync();

        await using var leaseB = await b.WaitAsync(Long, Ct);
        await using var leaseC = await c.WaitAsync(Long, Ct);
        Assert.NotNull(leaseB);
        Assert.NotNull(leaseC);
    }

    [Fact]
    public async Task CancelledAfterAdmission_ReleasesLeaseAndThrows()
    {
        var scheduler = new DriveAwareScheduler(new IdListResolver());
        var first = await scheduler.AcquireAsync("disk:1", "disk:2", Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // Race simulation: by the time the waiting job is told to wait, the blocker has finished AND the caller cancelled.
        scheduler.Waiting += (_, _) =>
        {
            first.DisposeAsync().AsTask().GetAwaiter().GetResult();
            cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.AcquireAsync("disk:1", "disk:3", cts.Token));

        // The lease it briefly held must have been released.
        await using var next = await scheduler.AcquireAsync("disk:1", "disk:4", Ct).WaitAsync(Long, Ct);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task WaitingIsNotRaised_ForJobThatAlreadyStarted()
    {
        var scheduler = new DriveAwareScheduler(new IdListResolver());
        var raised = 0;
        scheduler.Waiting += (_, _) => raised++;

        await using var a = await scheduler.AcquireAsync("disk:1", "disk:2", Ct);
        await using var b = await scheduler.AcquireAsync("disk:3", "disk:4", Ct);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Stress_OverlappingJobsAreNeverActiveTogether_AndNothingHangs()
    {
        const int Devices = 6;
        var scheduler = new DriveAwareScheduler(new IdListResolver());
        var active = new int[Devices];
        var violations = 0;
        var completed = 0;

        async Task Checked(int seed)
        {
            var rng = new Random(seed);
            for (var i = 0; i < 200; i++)
            {
                var a = rng.Next(Devices);
                var b = rng.Next(Devices);
                var set = new[] { a, b }.Distinct().ToArray();
                await using var lease = await scheduler.AcquireAsync("disk:" + a, "disk:" + b, CancellationToken.None);
                foreach (var d in set)
                {
                    if (Interlocked.Increment(ref active[d]) != 1)
                    {
                        Interlocked.Increment(ref violations);
                    }
                }

                await Task.Yield();
                foreach (var d in set)
                {
                    Interlocked.Decrement(ref active[d]);
                }

                Interlocked.Increment(ref completed);
            }
        }

        var workers = Enumerable.Range(0, 8).Select(t => Task.Run(() => Checked(t + 1), Ct)).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(0, violations);
        Assert.Equal(8 * 200, completed);
    }
}
