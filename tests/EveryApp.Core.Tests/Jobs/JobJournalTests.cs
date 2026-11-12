using System.Text;
using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

public sealed class JobJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "everyapp-journal-" + Guid.NewGuid().ToString("N"));

    public JobJournalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N") + ".journal");

    private static JournalRecord Done(string rel) =>
        new(JournalRecordKind.FileDone, rel, 10, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "abc", null);

    private static JournalRecord Of(JournalRecordKind kind, string? rel = null, string? detail = null) =>
        new(kind, rel, 0, DateTime.UnixEpoch, null, detail);

    private Guid MakeJournal(params string[] donePaths)
    {
        var id = Guid.NewGuid();
        using var j = JobJournal.Create(_dir, id, "{\"x\":1}");
        foreach (var p in donePaths)
        {
            j.Append(Done(p));
        }
        return id;
    }

    [Fact]
    public void Journal_AppendThenOpen_RestoresCompleted()
    {
        var id = MakeJournal(@"a\1.txt", @"a\2.txt");

        using var j = JobJournal.Open(_dir, id);

        Assert.Equal("{\"x\":1}", j.OptionsJson);
        Assert.Equal(2, j.CompletedPaths.Count);
        Assert.Contains(@"a\1.txt", j.CompletedPaths);
        Assert.Contains(@"a\2.txt", j.CompletedPaths);
        Assert.False(j.IsFinished);
    }

    [Fact]
    public void Journal_TruncatedLastLine_IsIgnoredOnResume()
    {
        var id = MakeJournal("1.txt", "2.txt", "3.txt");
        var path = PathFor(id);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^15]); // cut mid-line, no trailing newline

        using var j = JobJournal.Open(_dir, id);

        Assert.Equal(2, j.CompletedPaths.Count);
        Assert.DoesNotContain("3.txt", j.CompletedPaths);
    }

    [Fact]
    public void Journal_CorruptMiddleLine_StopsAtCorruption()
    {
        var id = MakeJournal("1.txt", "2.txt", "3.txt");
        var path = PathFor(id);
        var lines = File.ReadAllLines(path);
        lines[2] = lines[2].Replace("2.txt", "9.txt"); // header=0, 1.txt=1, 2.txt=2: CRC no longer matches
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        using var j = JobJournal.Open(_dir, id);

        Assert.Equal(["1.txt"], j.CompletedPaths.ToArray());
    }

    [Fact]
    public void Journal_CorruptTail_OpenTruncates_NewRecordsSurviveNextReopen()
    {
        var id = MakeJournal("1.txt", "2.txt");
        var path = PathFor(id);
        File.AppendAllText(path, "{\"crc\":1,\"r\":garbage-with-no-newline");

        using (var j = JobJournal.Open(_dir, id))
        {
            Assert.Equal(2, j.CompletedPaths.Count);
            j.Append(Done("3.txt"));
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(3, again.CompletedPaths.Count);
        Assert.Contains("3.txt", again.CompletedPaths);
    }

    [Fact]
    public void Journal_CorruptTailWithNewline_OpenTruncates_NewRecordsSurviveNextReopen()
    {
        var id = MakeJournal("1.txt");
        File.AppendAllText(PathFor(id), "not json at all\n");

        using (var j = JobJournal.Open(_dir, id))
        {
            j.Append(Done("2.txt"));
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(2, again.CompletedPaths.Count);
    }

    [Fact]
    public void Journal_BadCrcOnly_StopsThere()
    {
        var id = MakeJournal("1.txt", "2.txt");
        var path = PathFor(id);
        var text = File.ReadAllText(path);
        var firstFile = text.IndexOf("\"crc\":", text.IndexOf('\n'), StringComparison.Ordinal);
        File.WriteAllText(path, text.Insert(firstFile + 6, "9")); // alters the CRC digits of the first FileDone line

        using var j = JobJournal.Open(_dir, id);

        Assert.Empty(j.CompletedPaths);
    }

    [Fact]
    public void Journal_CompletedPaths_AreCaseInsensitive()
    {
        var id = MakeJournal(@"Docs\Report.TXT");

        using var j = JobJournal.Open(_dir, id);

        Assert.Contains(@"docs\report.txt", j.CompletedPaths);
    }

    [Fact]
    public void Journal_OnlyFileDoneCountsAsCompleted()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Of(JournalRecordKind.FileSkipped, "s.txt"));
            j.Append(Of(JournalRecordKind.FileFailed, "f.txt", "access denied"));
            j.Append(Done("d.txt"));
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(["d.txt"], again.CompletedPaths.ToArray());
    }

    [Fact]
    public void Journal_JobFinished_SetsIsFinished_AndSurvivesReopen()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            Assert.False(j.IsFinished);
            j.Append(Of(JournalRecordKind.JobFinished));
            Assert.True(j.IsFinished);
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.True(again.IsFinished);
    }

    [Fact]
    public void Journal_Create_WhenFileExists_Throws()
    {
        var id = MakeJournal();
        Assert.Throws<IOException>(() => JobJournal.Create(_dir, id, "{}"));
    }

    [Fact]
    public void Journal_Open_WhenMissing_ThrowsClearException()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => JobJournal.Open(_dir, Guid.NewGuid()));
        Assert.Contains("journal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Journal_Open_WhenHeaderIsBad_ThrowsInvalidData()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(PathFor(id), "garbage\n");
        Assert.Throws<InvalidDataException>(() => JobJournal.Open(_dir, id));
    }

    [Fact]
    public void Journal_Open_WhenEmptyFile_ThrowsInvalidData()
    {
        var id = Guid.NewGuid();
        File.WriteAllBytes(PathFor(id), []);
        Assert.Throws<InvalidDataException>(() => JobJournal.Open(_dir, id));
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"a\..\..\evil.txt")]
    [InlineData("a/../b.txt")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\rooted.txt")]
    public void Journal_TamperedUnsafePath_IsTreatedAsCorruption(string unsafePath)
    {
        // Forge a well-formed, correctly checksummed line, so only path validation can stop it.
        var id = MakeJournal("1.txt");
        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, unsafePath));
        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "after.txt"));

        using var j = JobJournal.Open(_dir, id);

        Assert.Equal(["1.txt"], j.CompletedPaths.ToArray());
    }

    [Fact]
    public void Journal_ForgeHelperProducesAcceptedLines()
    {
        var id = MakeJournal("1.txt");
        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "forged-ok.txt"));

        using var j = JobJournal.Open(_dir, id);

        Assert.Contains("forged-ok.txt", j.CompletedPaths);
    }

    [Fact]
    public void Journal_OverlongLine_IsTreatedAsCorruption()
    {
        var id = MakeJournal("1.txt");
        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "big.txt", new string('x', 70_000)));
        File.AppendAllBytes(PathFor(id), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "after.txt"));

        using var j = JobJournal.Open(_dir, id);

        Assert.Equal(["1.txt"], j.CompletedPaths.ToArray());
    }

    [Fact]
    public void Journal_Append_RejectsUnsafePathAndOversizeRecord()
    {
        using var j = JobJournal.Create(_dir, Guid.NewGuid(), "{}");
        Assert.Throws<ArgumentException>(() => j.Append(Done(@"..\x")));
        Assert.Throws<ArgumentException>(() => j.Append(Of(JournalRecordKind.FileFailed, "ok.txt", new string('y', 70_000))));
    }

    [Fact]
    public void Journal_FileFailedAndJobFinished_AreDurableWithoutDispose()
    {
        var id = Guid.NewGuid();
        using var j = JobJournal.Create(_dir, id, "{}");
        j.Append(Done("1.txt")); // buffered only

        j.Append(Of(JournalRecordKind.FileFailed, "f.txt", "boom"));

        // A second, independent reader sees both records although the journal is still open.
        Assert.Equal(3, CountLinesShared(PathFor(id)));

        j.Append(Done("2.txt"));
        j.Append(Of(JournalRecordKind.JobFinished));
        Assert.Equal(5, CountLinesShared(PathFor(id)));
    }

    [Fact]
    public void Journal_FlushesEvery64Records()
    {
        var id = Guid.NewGuid();
        using var j = JobJournal.Create(_dir, id, "{}");
        for (var i = 0; i < 64; i++)
        {
            j.Append(Done($"f{i}.txt"));
        }

        Assert.Equal(65, CountLinesShared(PathFor(id)));
    }

    [Fact]
    public void Journal_BelowThreshold_IsNotYetOnDisk_ThenDisposeFlushes()
    {
        var id = Guid.NewGuid();
        var j = JobJournal.Create(_dir, id, "{}");
        j.Append(Done("1.txt"));
        Assert.Equal(1, CountLinesShared(PathFor(id))); // header only: this record is still buffered

        j.Dispose();
        Assert.Equal(2, CountLinesShared(PathFor(id)));
    }

    [Fact]
    public void Journal_Dispose_IsIdempotent_AndAppendAfterDisposeThrows()
    {
        var j = JobJournal.Create(_dir, Guid.NewGuid(), "{}");
        j.Dispose();
        j.Dispose();
        Assert.Throws<ObjectDisposedException>(() => j.Append(Done("x.txt")));
    }

    [Fact]
    public void Journal_ConcurrentAppends_AreAllRecordedWithoutInterleaving()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            Parallel.For(0, 8, t =>
            {
                for (var i = 0; i < 250; i++)
                {
                    j.Append(Done($"t{t}\\f{i}.txt"));
                }
            });
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(2000, again.CompletedPaths.Count);
    }

    [Fact]
    public void Journal_NonAsciiPathsAndDetails_RoundTrip()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{\"name\":\"caf\u00e9\"}"))
        {
            j.Append(Done("d\u00e9j\u00e0\\\u30c6\u30b9\u30c8.txt"));
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal("{\"name\":\"caf\u00e9\"}", again.OptionsJson);
        Assert.Contains("d\u00e9j\u00e0\\\u30c6\u30b9\u30c8.txt", again.CompletedPaths);
    }

    private static int CountLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd().Count(c => c == '\n');
    }
}
