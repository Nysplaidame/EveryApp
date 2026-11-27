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

    [Fact]
    public async Task SlowSink_SnapshotsStillArriveInTheOrderTheyWereBuilt()
    {
        var delivered = new List<JobProgress>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        var sink = new ListProgress
        {
            OnReport = p =>
            {
                if (Volatile.Read(ref armed) == 1 && Interlocked.Exchange(ref armed, 2) == 1)
                {
                    entered.SetResult(); // the first snapshot built after arming is held up inside the sink...
                    gate.Task.Wait(TestContext.Current.CancellationToken);
                }

                lock (delivered)
                {
                    delivered.Add(p);
                }
            },
        };
        var tracker = new ProgressTracker(sink, TimeProvider.System);
        var a = tracker.StartFile("a");
        var b = tracker.StartFile("b");
        Volatile.Write(ref armed, 1);

        var slow = Task.Run(() => a.Bytes.Report(new FileProgress("a", 10, 1000)), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        b.Bytes.Report(new FileProgress("b", 20, 1000)); // ...while a later snapshot (more bytes) is built by another thread
        gate.SetResult();
        await slow.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        long[] bytes;
        lock (delivered)
        {
            bytes = [.. delivered.Select(p => p.BytesDone).Skip(2)]; // after the two start reports
        }

        Assert.Equal(bytes.Order(), bytes); // never goes down
        Assert.Equal(30, bytes[^1]); // and the newest one is not lost
    }
}
