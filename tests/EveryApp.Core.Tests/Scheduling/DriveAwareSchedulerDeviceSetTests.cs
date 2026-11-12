using EveryApp.Core.Scheduling;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Scheduling;

/// <summary>A job with several sources acquires one device set covering all of them plus the destination.</summary>
public sealed class DriveAwareSchedulerDeviceSetTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeviceSet_UnionOfSources_BlocksAJobOnTheSecondSourceDisk()
    {
        var scheduler = new DriveAwareScheduler(new FakeDeviceResolver().Map("other", "disk:2"));
        await using var job = await scheduler.AcquireAsync(DeviceSet.Of("disk:1", "disk:2", "disk:9"), "two sources", Ct);

        var second = scheduler.AcquireAsync("other", "other", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(Short, Ct));
    }

    [Fact]
    public async Task DeviceSet_Disjoint_RunsInParallel()
    {
        var scheduler = new DriveAwareScheduler(new FakeDeviceResolver());
        await using var a = await scheduler.AcquireAsync(DeviceSet.Of("disk:1"), "a", Ct);
        await using var b = await scheduler.AcquireAsync(DeviceSet.Of("disk:2"), "b", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task PathOverload_StillWorks_ThroughTheSetOverload()
    {
        var scheduler = new DriveAwareScheduler(new FakeDeviceResolver().Map("s", "disk:1").Map("d", "disk:2"));
        await using var held = await scheduler.AcquireAsync(DeviceSet.Of("disk:2"), "held", Ct);

        var waiting = scheduler.AcquireAsync("s", "d", Ct);

        await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(Short, Ct));
    }
}
