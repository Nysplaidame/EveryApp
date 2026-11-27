using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

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

    [Fact]
    public void JournalCannotBeWritten_JobStopsWithOnePlainProblem_NothingThrown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ferry-journal-writer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var io = new JournalIo();
            using var journal = JobJournal.CreateWithIo(dir, Guid.NewGuid(), "{}", null, io);
            io.Write = (_, _) => throw new IOException("The disk is full.");
            var outcome = new JobOutcome(verifyOn: true);
            var writer = new JobJournalWriter(journal, outcome);

            writer.Done(@"0\a.txt", 1, DateTime.UtcNow, null); // the write fails
            writer.Done(@"0\b.txt", 1, DateTime.UtcNow, null); // the journal is now faulted
            var recorded = writer.TryAppend(new JournalRecord(
                JournalRecordKind.FilePartStarted, @"0\c.txt", 1, DateTime.UtcNow, null, "c.txt.0123abcd.ferrypart", "c.txt"));

            Assert.False(recorded, "No part file may be made once the journal cannot record it.");
            Assert.True(outcome.Stopped, "No new file starts once the journal cannot be written.");
            Assert.Equal(CopyMessages.JournalWriteFailed, Assert.Single(outcome.Problems.Items).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
