namespace Ferry.Cli.Tests;

public sealed class CancellationTests
{
    [Fact]
    public void FirstSignalCancels_SecondSignalExitsImmediately()
    {
        using var source = new CancellationTokenSource();
        var forced = false;
        var signals = new CancellationSignals(source, () => forced = true);
        signals.Signal();
        Assert.True(source.IsCancellationRequested);
        Assert.False(forced);
        signals.Signal();
        Assert.True(forced);
    }
}
