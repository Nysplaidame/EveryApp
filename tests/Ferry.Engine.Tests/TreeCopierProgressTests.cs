using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Progress, keep-awake, scheduler, cancellation and the speed average.</summary>
public sealed class TreeCopierProgressTests : TreeCopierTestBase
{
    [Fact]
    public async Task Progress_Phases_InOrder()
    {
        Add(@"C:\src\a.bin", 3000);
        Add(@"C:\src\b.bin", 5000);
        var progress = new ListProgress();

        await _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress);

        var phases = new List<string>();
        foreach (var r in progress.Reports)
        {
            if (phases.Count == 0 || phases[^1] != r.Phase)
            {
                phases.Add(r.Phase);
            }
        }

        Assert.Equal("Checking", phases[0]);
        var firstCopy = phases.IndexOf("Copying");
        Assert.True(firstCopy > 0);
        Assert.True(phases.IndexOf("Verifying") > firstCopy);
        Assert.DoesNotContain("Checking", phases.Skip(firstCopy));
        Assert.All(phases, p => Assert.True(p is "Checking" or "Waiting" or "Copying" or "Verifying", p));
        var last = progress.Reports[^1];
        Assert.Equal(8000, last.BytesTotal);
        Assert.Equal(8000, last.BytesDone);
        Assert.Equal(2, last.FilesDone);
        Assert.Equal(2, last.FilesTotal);
    }

    [Fact]
    public async Task Progress_ShowsWaiting_WhileTheDriveIsBusy()
    {
        Add(@"C:\src\a.bin", 100);
        _rig.Devices.Map(@"C:\src", "disk-c").Map(@"D:\dst", "disk-d").Map(@"D:\other", "disk-d");
        var other = await _rig.Scheduler.AcquireAsync(@"D:\other", @"D:\other", Ct);
        var progress = new ListProgress();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.OnReport = p =>
        {
            if (p.Phase == "Waiting")
            {
                waiting.TrySetResult();
            }
        };

        var run = Task.Run(() => _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress), Ct);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        await other.DisposeAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(1, result.Copied);
    }

    [Fact]
    public async Task Scheduler_CoversEverySource_NotJustTheFirst()
    {
        AddText(@"C:\a\x.txt", "x");
        AddText(@"E:\b\y.txt", "y");
        _rig.Devices.Map(@"C:\a", "disk-c").Map(@"E:\b", "disk-e").Map(@"D:\dst", "disk-d").Map(@"E:\other", "disk-e");
        var other = await _rig.Scheduler.AcquireAsync(@"E:\other", @"E:\other", Ct); // another job busy on the second source's disk only
        var progress = new ListProgress();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.OnReport = p =>
        {
            if (p.Phase == "Waiting")
            {
                waiting.TrySetResult();
            }
        };

        var run = Task.Run(() => _rig.RunAsync([@"C:\a", @"E:\b"], @"D:\dst", progress: progress), Ct);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        await other.DisposeAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(2, result.Copied);
    }

    [Fact]
    public async Task Run_PreventsSleepWhileActive()
    {
        Add(@"C:\src\a.bin", 100);
        var activeDuringCopy = -1;
        _rig.Fs.AfterCreateWrite = _ => activeDuringCopy = _rig.Sleep.Active;

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, activeDuringCopy);
        Assert.Equal(1, _rig.Sleep.Acquired);
        Assert.Equal(1, _rig.Sleep.Released);
        Assert.Equal(0, _rig.Sleep.Active);
    }

    [Fact]
    public async Task Cancel_ReleasesEverything_AndLeavesJobResumable()
    {
        Add(@"C:\src\a.bin", 100);
        Add(@"C:\src\b.bin", 200);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _rig.Fs.AfterCreateWrite = path =>
        {
            if (IsPartOf(path, "b.bin"))
            {
                cts.Cancel();
            }
        };
        var id = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id, ct: cts.Token));

        Assert.Equal(0, _rig.Sleep.Active);
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        await using (await _rig.Scheduler.AcquireAsync(@"C:\src", @"D:\dst", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
            // the scheduler slot was given back
        }

        _rig.Fs.AfterCreateWrite = null;
        var resumed = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);
        Assert.Equal(1, resumed.Copied);
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\b.bin"));
    }

    [Fact]
    public void SpeedWindow_AveragesTheLastTenSeconds()
    {
        var window = new SpeedWindow(TimeSpan.TicksPerSecond);
        long bytes = 0;
        for (var s = 0; s <= 20; s++)
        {
            window.Add(s * TimeSpan.TicksPerSecond, bytes);
            bytes += 1_000_000; // 1 MB/s for 20 s
        }

        for (var s = 21; s <= 30; s++)
        {
            bytes += 3_000_000; // then 3 MB/s
            window.Add(s * TimeSpan.TicksPerSecond, bytes);
        }

        Assert.InRange(window.BytesPerSecond, 2_900_000, 3_100_000);
    }

    [Fact]
    public void SpeedWindow_NoSpeedYet_IsZero()
    {
        var window = new SpeedWindow(TimeSpan.TicksPerSecond);
        window.Add(0, 0);
        Assert.Equal(0, window.BytesPerSecond);
    }
}
