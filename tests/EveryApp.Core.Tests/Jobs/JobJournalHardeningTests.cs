using System.Text;
using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

public sealed class JobJournalHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "everyapp-journal-h-" + Guid.NewGuid().ToString("N"));

    public JobJournalHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N") + ".journal");

    private static JournalRecord Rec(JournalRecordKind kind, string? rel, string? detail = null) =>
        new(kind, rel, 1, DateTime.UnixEpoch, null, detail);

    private static JournalRecord Done(string rel) => Rec(JournalRecordKind.FileDone, rel);

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void AdvanceMilliseconds(long ms) => Interlocked.Add(ref _ticks, ms);
    }

    public static TheoryData<JournalRecord> InvalidRecords() => new()
    {
        Rec(JournalRecordKind.JobStarted, null, "{}"),
        Rec(JournalRecordKind.FileDone, null),
        Rec(JournalRecordKind.FileSkipped, null),
        Rec(JournalRecordKind.FileFailed, null, "x"),
        Rec(JournalRecordKind.FileSkipped, @"..\x"),
        Rec(JournalRecordKind.FileFailed, "a.txt:stream", "x"),
        Rec((JournalRecordKind)99, "a.txt"),
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void Append_InvalidRecord_Throws_AndNothingIsLost(JournalRecord invalid)
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Done("before.txt"));
            Assert.Throws<ArgumentException>(() => j.Append(invalid));
            j.Append(Done("after.txt"));
            Assert.Equal(2, j.CompletedPaths.Count);
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(2, again.CompletedPaths.Count);
        Assert.Contains("before.txt", again.CompletedPaths);
        Assert.Contains("after.txt", again.CompletedPaths);
    }

    [Theory]
    [InlineData("C:foo")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\server\x")]
    [InlineData("a\0b.txt")]
    [InlineData("a\u0007b.txt")]
    [InlineData("a.txt:s")]
    [InlineData(".. ")]
    [InlineData("...")]
    [InlineData(@"a\. \b")]
    [InlineData("a/../b")]
    [InlineData(".")]
    [InlineData("")]
    public void Append_UnsafePath_Throws(string path)
    {
        using var j = JobJournal.Create(_dir, Guid.NewGuid(), "{}");
        Assert.Throws<ArgumentException>(() => j.Append(Done(path)));
    }

    [Fact]
    public void Paths_SlashAndBackslash_AreTheSame()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Done("a/b.txt"));
            Assert.Contains(@"a\b.txt", j.CompletedPaths);
        }

        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "c/d.txt"));
        using var again = JobJournal.Open(_dir, id);
        Assert.Contains(@"a\b.txt", again.CompletedPaths);
        Assert.Contains(@"c\d.txt", again.CompletedPaths);
        Assert.Equal(2, again.CompletedPaths.Count);
    }

    [Fact]
    public void WriteFailsMidRecord_JournalIsFaulted_AndReopenRecoversEarlierRecords()
    {
        var id = Guid.NewGuid();
        var io = new JournalIo();
        var j = JobJournal.CreateWithIo(_dir, id, "{}", null, io);
        j.Append(Done("1.txt"));
        j.Append(Done("2.txt"));

        io.Write = (stream, line) =>
        {
            stream.Write(line, 0, line.Length / 2);
            throw new IOException("disk full");
        };
        var original = Assert.Throws<IOException>(() => j.Append(Done("3.txt")));
        Assert.Equal("disk full", original.Message);

        io.Write = JournalIo.DefaultWrite; // even a healthy disk must not let the journal continue
        var faulted = Assert.Throws<InvalidOperationException>(() => j.Append(Done("4.txt")));
        Assert.Contains("faulted", faulted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(original, faulted.InnerException);
        Assert.DoesNotContain("3.txt", j.CompletedPaths);
        Assert.DoesNotContain("4.txt", j.CompletedPaths);
        j.Dispose();

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(["1.txt", "2.txt"], again.CompletedPaths.Order().ToArray());
        again.Append(Done("5.txt")); // and the file is clean enough to carry on
        again.Dispose();
        using var third = JobJournal.Open(_dir, id);
        Assert.Contains("5.txt", third.CompletedPaths);
    }

    [Fact]
    public void FlushFails_JournalIsFaulted_FailedRecordIsRemoved()
    {
        var id = Guid.NewGuid();
        var io = new JournalIo();
        var j = JobJournal.CreateWithIo(_dir, id, "{}", null, io);
        j.Append(Done("1.txt"));

        io.Flush = _ => throw new IOException("flush failed");
        Assert.Throws<IOException>(() => j.Append(Rec(JournalRecordKind.FileFailed, "f.txt", "boom")));
        Assert.Throws<InvalidOperationException>(() => j.Append(Done("2.txt")));
        j.Dispose();

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(["1.txt"], again.CompletedPaths.ToArray());
    }

    [Fact]
    public void TwoSecondRule_UsesInjectedClock()
    {
        var id = Guid.NewGuid();
        var clock = new FakeTime();
        using var j = JobJournal.Create(_dir, id, "{}", clock);
        j.Append(Done("1.txt"));

        clock.AdvanceMilliseconds(1900);
        j.Append(Done("2.txt"));
        Assert.Equal(1, CountLinesShared(PathFor(id))); // under 2 s: header only on disk

        clock.AdvanceMilliseconds(200);
        j.Append(Done("3.txt"));
        Assert.Equal(4, CountLinesShared(PathFor(id)));
    }

    [Fact]
    public void ValidLastLineWithoutFinalNewline_IsDroppedAndTruncated()
    {
        // A record is only trusted once its newline is on disk: a crash can cut a line anywhere, and
        // "looks complete" cannot be told apart from "cut exactly before the newline" without that marker.
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Done("1.txt"));
        }

        var forged = JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "2.txt");
        File.AppendAllBytes(PathFor(id), forged[..^1]);

        using (var j = JobJournal.Open(_dir, id))
        {
            Assert.Equal(["1.txt"], j.CompletedPaths.ToArray());
            j.Append(Done("3.txt"));
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(["1.txt", "3.txt"], again.CompletedPaths.Order().ToArray());
    }

    [Fact]
    public void CrlfLines_AreCorruptionAtTheFirstCrlfLine()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Done("1.txt"));
            j.Append(Done("2.txt"));
            j.Append(Done("3.txt"));
        }

        var lines = File.ReadAllText(PathFor(id)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // header and 1.txt keep LF; 2.txt and 3.txt end with CRLF.
        File.WriteAllText(PathFor(id), lines[0] + "\n" + lines[1] + "\n" + lines[2] + "\r\n" + lines[3] + "\r\n");

        using var j2 = JobJournal.Open(_dir, id);
        Assert.Equal(["1.txt"], j2.CompletedPaths.ToArray());
    }

    [Fact]
    public async Task IsFinished_IsVisibleAcrossThreads()
    {
        using var j = JobJournal.Create(_dir, Guid.NewGuid(), "{}");
        await Task.Run(() => j.Append(Rec(JournalRecordKind.JobFinished, null)), TestContext.Current.CancellationToken);
        Assert.True(j.IsFinished);
    }

    private static int CountLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd().Count(c => c == '\n');
    }
}
