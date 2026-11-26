namespace Ferry.Engine.Tests;

/// <summary>Progress tracker with several files in flight at once.</summary>
public sealed class ProgressTrackerTests
{
    private static (ProgressTracker Tracker, ListProgress Sink) Make()
    {
        var sink = new ListProgress();
        return (new ProgressTracker(sink, TimeProvider.System), sink);
    }

    [Fact]
    public void TwoSlots_BytesAddUp()
    {
        var (tracker, sink) = Make();
        var a = tracker.StartFile("a");
        var b = tracker.StartFile("b");
        a.Bytes.Report(new FileProgress("a", 100, 1000));
        b.Bytes.Report(new FileProgress("b", 50, 1000));

        var last = sink.Reports[^1];
        Assert.Equal(150, last.BytesDone);
        Assert.Equal(2, last.InFlight);
        Assert.Equal("b", last.CurrentFile);
    }

    [Fact]
    public void EndOne_CurrentFileFallsBackToOneStillRunning()
    {
        var (tracker, sink) = Make();
        var a = tracker.StartFile("a");
        var b = tracker.StartFile("b");
        a.Bytes.Report(new FileProgress("a", 100, 1000));
        b.Bytes.Report(new FileProgress("b", 50, 1000));

        b.End(400);

        var last = sink.Reports[^1];
        Assert.Equal("a", last.CurrentFile);
        Assert.Equal(1, last.InFlight);
        Assert.Equal(400 + 100, last.BytesDone);
    }

    [Fact]
    public void Reset_OnlyClearsThatSlot()
    {
        var (tracker, sink) = Make();
        var a = tracker.StartFile("a");
        var b = tracker.StartFile("b");
        a.Bytes.Report(new FileProgress("a", 100, 1000));
        b.Bytes.Report(new FileProgress("b", 50, 1000));

        a.Reset();
        tracker.Report();

        Assert.Equal(50, sink.Reports[^1].BytesDone);
    }

    [Fact]
    public void Phase_VerifyingOnlyWhenAllInFlightVerify()
    {
        var (tracker, sink) = Make();
        tracker.SetPhase("Copying");
        var a = tracker.StartFile("a");
        var b = tracker.StartFile("b");

        a.Verifying();
        Assert.Equal("Copying", sink.Reports[^1].Phase);

        b.Verifying();
        Assert.Equal("Verifying", sink.Reports[^1].Phase);

        a.End(1);
        b.End(1);
        Assert.Equal("Copying", sink.Reports[^1].Phase);
    }

    [Fact]
    public void End_IsIdempotent()
    {
        var (tracker, sink) = Make();
        var a = tracker.StartFile("a");

        a.End(10);
        a.End(10);

        var last = sink.Reports[^1];
        Assert.Equal(1, last.FilesDone);
        Assert.Equal(10, last.BytesDone);
        Assert.Equal(0, last.InFlight);
        Assert.Null(last.CurrentFile);
    }

    [Fact]
    public void StatusLine_ClearsWhenSlotEnds()
    {
        var (tracker, sink) = Make();
        var a = tracker.StartFile("a");
        tracker.SetPhase("Waiting for the network drive…");
        Assert.Equal("Waiting for the network drive…", sink.Reports[^1].Phase);

        a.End(1);

        Assert.Equal("Copying", sink.Reports[^1].Phase);
    }
}
