using EveryApp.Core.FileSystem;

namespace Ferry.Engine.Tests;

public sealed class JobOutcomeTests
{
    private const int Tasks = 8;
    private const int PerTask = 10_000;

    [Fact]
    public async Task ConcurrentAdds_AreAllCounted()
    {
        var outcome = new JobOutcome(verifyOn: true);

        await Task.WhenAll(Enumerable.Range(0, Tasks).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < PerTask; i++)
            {
                outcome.AddCopied();
                outcome.AddSkipped();
                outcome.AddFailed();
                outcome.AddVerified();
                outcome.Problems.Add(new Problem("p", IoErrorKind.None, "m"));
            }
        })));

        Assert.Equal(Tasks * PerTask, outcome.Copied);
        Assert.Equal(Tasks * PerTask, outcome.Skipped);
        Assert.Equal(Tasks * PerTask, outcome.Failed);
        Assert.Equal(Tasks * PerTask, outcome.Verified);
        Assert.Equal(Tasks * PerTask, outcome.Problems.Count);
        Assert.Equal(JobOutcome.DefaultMaxProblems, outcome.Problems.Items.Count);
        Assert.Equal((Tasks * PerTask) - JobOutcome.DefaultMaxProblems, outcome.Problems.More);
    }

    [Fact]
    public void Flags_AreSetByMethods()
    {
        var outcome = new JobOutcome(verifyOn: true);

        outcome.MarkVerifyFailed();
        outcome.MarkStopped();
        outcome.AddFailed(3);

        Assert.True(outcome.VerifyFailed);
        Assert.True(outcome.Stopped);
        Assert.Equal(3, outcome.Failed);
    }
}
