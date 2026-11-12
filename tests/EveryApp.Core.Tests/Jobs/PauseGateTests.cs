using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

public sealed class PauseGateTests
{
    private static bool IsDone(ValueTask task) => task.IsCompletedSuccessfully;

    [Fact]
    public async Task PauseGate_BlocksUntilResume()
    {
        var gate = new PauseGate();
        gate.Pause();
        Assert.True(gate.IsPaused);

        var wait = gate.WaitIfPausedAsync(CancellationToken.None).AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        gate.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task PauseGate_CancelWhilePaused_Throws()
    {
        var gate = new PauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitIfPausedAsync(cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void PauseGate_PauseAndResume_AreIdempotent()
    {
        var gate = new PauseGate();
        gate.Resume();
        Assert.False(gate.IsPaused);
        gate.Pause();
        gate.Pause();
        Assert.True(gate.IsPaused);
        gate.Resume();
        gate.Resume();
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task PauseGate_ResumeReleasesAllWaiters()
    {
        var gate = new PauseGate();
        gate.Pause();
        var waits = Enumerable.Range(0, 5).Select(_ => gate.WaitIfPausedAsync(CancellationToken.None).AsTask()).ToArray();
        gate.Resume();
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PauseGate_NotPaused_CompletesSynchronouslyWithoutAllocating()
    {
        var gate = new PauseGate();

        for (var i = 0; i < 10; i++)
        {
            Assert.True(IsDone(gate.WaitIfPausedAsync(CancellationToken.None))); // warm up
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(IsDone(gate.WaitIfPausedAsync(CancellationToken.None)));
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public async Task PauseGate_AlreadyCancelled_ThrowsEvenWhenNotPaused()
    {
        var gate = new PauseGate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.WaitIfPausedAsync(cts.Token));
    }
}
