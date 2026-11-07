using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

/// <summary>FilePartStarted records (R29): the journal names every part file before it is created, so resume can delete exactly those.</summary>
public sealed class JobJournalPartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "everyapp-journal-p-" + Guid.NewGuid().ToString("N"));

    public JobJournalPartTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static JournalRecord Part(string? rel, string? part) =>
        new(JournalRecordKind.FilePartStarted, rel, 0, DateTime.UnixEpoch, null, part);

    [Fact]
    public void PartRecords_RoundTrip_AndAreListedInOrder()
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            j.Append(Part(@"photos\a.jpg", @"photos\a.jpg.0011aabb.ferrypart"));
            j.Append(Part("b.txt", "b (2).txt.ffee0011.ferrypart"));
            Assert.Equal(2, j.PartFiles.Count);
        }

        using var again = JobJournal.Open(_dir, id);
        Assert.Equal(
            [new JournalPart(@"photos\a.jpg", @"photos\a.jpg.0011aabb.ferrypart"), new JournalPart("b.txt", "b (2).txt.ffee0011.ferrypart")],
            again.PartFiles);
        Assert.Empty(again.CompletedPaths);
    }

    public static TheoryData<string?, string?> InvalidParts() => new()
    {
        { null, "a.ferrypart" },
        { "a.txt", null },
        { "a.txt", "a.txt.tmp" },
        { "a.txt", @"..\a.txt.0011aabb.ferrypart" },
        { "a.txt", @"C:\a.txt.0011aabb.ferrypart" },
        { "a.txt", "a.txt:s.ferrypart" },
        { @"..\a.txt", "a.txt.0011aabb.ferrypart" },
    };

    [Theory]
    [MemberData(nameof(InvalidParts))]
    public void PartRecord_Unsafe_RejectedOnAppend_AndStopsLoad(string? rel, string? part)
    {
        var id = Guid.NewGuid();
        using (var j = JobJournal.Create(_dir, id, "{}"))
        {
            Assert.Throws<ArgumentException>(() => j.Append(Part(rel, part)));
        }

        // A forged line with the same content is not trusted on load either (Append and Load share one rule set).
        File.AppendAllBytes(Path.Combine(_dir, id.ToString("N") + ".journal"), JournalTestHelper.ForgeLine(JournalRecordKind.FilePartStarted, rel, part));
        File.AppendAllBytes(Path.Combine(_dir, id.ToString("N") + ".journal"), JournalTestHelper.ForgeLine(JournalRecordKind.FileDone, "after.txt"));
        using var again = JobJournal.Open(_dir, id);
        Assert.Empty(again.PartFiles);
        Assert.Empty(again.CompletedPaths);
    }

    [Fact]
    public void PartRecord_IsHandedToTheOsAtOnce()
    {
        var handedOver = 0;
        var io = new JournalIo { HandOver = _ => handedOver++ };
        using var j = JobJournal.CreateWithIo(_dir, Guid.NewGuid(), "{}", null, io);
        j.Append(new JournalRecord(JournalRecordKind.FileDone, "x.txt", 1, DateTime.UnixEpoch, null, null));
        Assert.Equal(0, handedOver);
        j.Append(Part("a.txt", "a.txt.0011aabb.ferrypart"));
        Assert.Equal(1, handedOver);
    }
}
