namespace Ferry.Engine.Tests;

/// <summary>The copies still running: a name held by one copy can never be taken by another.</summary>
public sealed class InFlightCopiesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SecondStartOnSameTarget_Throws_FirstKept()
    {
        using var inFlight = new InFlightCopies(2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;

        await inFlight.WaitForPlaceAsync(TestContext.Current.CancellationToken);
        await inFlight.StartAsync(@"D:\dst\a.txt", () => gate.Task);
        await inFlight.WaitForPlaceAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => inFlight.StartAsync(@"D:\DST\A.TXT", () =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }));

        Assert.False(secondRan);
        Assert.True(inFlight.IsReserved(@"D:\dst\a.txt"));
        Assert.False(inFlight.EndOf(@"D:\dst\a.txt").IsCompleted); // still the first copy's reservation
        gate.SetResult();
        await inFlight.EndOf(@"D:\dst\a.txt").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await inFlight.WaitForAllAsync();
        inFlight.ThrowIfFailed();
    }
}
