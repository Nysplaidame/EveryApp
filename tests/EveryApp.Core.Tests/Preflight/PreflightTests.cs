using System.Diagnostics;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;
using EveryApp.Core.Preflight;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Preflight;

public sealed class PreflightTests
{
    private const string Dest = @"\\?\D:\Backup";

    private static WalkItem File(string rel, long length = 1) =>
        new(new FileEntry(@"\\?\C:\src\" + rel, length, FileAttributes.Normal, DateTime.UnixEpoch, DateTime.UnixEpoch, false, 0, 0), rel);

    private static WalkItem Dir(string rel) =>
        new(new FileEntry(@"\\?\C:\src\" + rel, 0, FileAttributes.Directory, DateTime.UnixEpoch, DateTime.UnixEpoch, false, 0, 0), rel);

    private static IReadOnlyList<PreflightIssue> Run(IReadOnlyList<WalkItem> items, string fsName = "NTFS", bool longPaths = false, long free = 1L << 40)
    {
        var fs = new InMemoryFileSystem { FreeSpaceBytes = free };
        return new PreflightRunner(fs).Check(items, Dest, fsName, longPaths);
    }

    private static List<PreflightIssue> Of(IEnumerable<PreflightIssue> issues, PreflightCode code) => issues.Where(i => i.Code == code).ToList();

    [Fact]
    public void PathTooLong_FiresAbove259WhenLongPathsOff()
    {
        // "D:\Backup\" is 10 chars, so a 249-char name gives 259 (fine) and 250 gives 260 (too long).
        var ok = Run([File(new string('a', 249))]);
        var bad = Run([File(new string('a', 250))]);
        Assert.Empty(Of(ok, PreflightCode.PathTooLong));
        var issue = Assert.Single(Of(bad, PreflightCode.PathTooLong));
        Assert.Contains("260", issue.Message);
    }

    [Fact]
    public void PathTooLong_NotReportedWhenLongPathsEnabled()
    {
        Assert.Empty(Of(Run([File(new string('a', 300))], longPaths: true), PreflightCode.PathTooLong));
    }

    [Fact]
    public void PathTooLong_FolderCountsTrailingSeparator()
    {
        Assert.Empty(Of(Run([Dir(new string('a', 248))]), PreflightCode.PathTooLong));
        Assert.Single(Of(Run([Dir(new string('a', 249))]), PreflightCode.PathTooLong));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("Aux.tar.gz")]
    [InlineData("com0")]
    [InlineData("PRN .txt")]
    [InlineData("COM\u00B2")]
    [InlineData("lpt\u00B3.log")]
    public void ReservedName_Flagged(string name)
    {
        var issue = Assert.Single(Of(Run([File(name)]), PreflightCode.ReservedName));
        Assert.EndsWith(name, issue.Path);
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("nulls.txt")]
    [InlineData("LPT")]
    public void ReservedName_NotFlaggedForSimilarNames(string name)
    {
        Assert.Empty(Of(Run([File(name)]), PreflightCode.ReservedName));
    }

    [Fact]
    public void ReservedName_ChecksEverySegment()
    {
        var issues = Of(Run([Dir("stuff"), Dir(@"stuff\con"), File(@"stuff\con\readme.txt")]), PreflightCode.ReservedName);
        Assert.Equal(2, issues.Count);
    }

    [Theory]
    [InlineData("a.")]
    [InlineData("b ")]
    public void TrailingDotOrSpace_Flagged(string name)
    {
        Assert.Single(Of(Run([File(name)]), PreflightCode.TrailingDotOrSpace));
    }

    [Fact]
    public void IllegalCharAndTrailingDot_ReportedOncePerPathEach()
    {
        var issues = Run([File("a:b.")], "SMB");
        Assert.Single(Of(issues, PreflightCode.TrailingDotOrSpace));
        Assert.Single(Of(issues, PreflightCode.IllegalCharForDestination));
        Assert.Equal(2, issues.Count);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    public void IllegalChar_FlaggedForSmb(string name)
    {
        Assert.Single(Of(Run([File(name)], "SMB"), PreflightCode.IllegalCharForDestination));
        Assert.Empty(Of(Run([File(name)], "NTFS"), PreflightCode.IllegalCharForDestination));
    }

    [Fact]
    public void CaseCollision_SameFolder()
    {
        var issues = Of(Run([File("A.txt"), File("a.txt"), File(@"sub\a.txt"), File(@"other\A.txt")]), PreflightCode.CaseCollision);
        Assert.Single(issues);
    }

    [Fact]
    public void CaseCollision_DifferentFoldersAreFine()
    {
        Assert.Empty(Of(Run([File(@"one\a.txt"), File(@"two\A.txt")]), PreflightCode.CaseCollision));
    }

    [Fact]
    public void CaseCollision_FolderPartsOfPathsCount()
    {
        var issues = Of(Run([Dir("Docs"), Dir("docs")]), PreflightCode.CaseCollision);
        Assert.Single(issues);
    }

    [Fact]
    public void TooLargeForFat32_FiresAtFourGiB()
    {
        var big = File("big.bin", 4_294_967_296);
        var justUnder = File("ok.bin", 4_294_967_295);
        Assert.Single(Of(Run([big, justUnder], "FAT32"), PreflightCode.TooLargeForFat32));
        Assert.Empty(Of(Run([big], "exFAT"), PreflightCode.TooLargeForFat32));
        Assert.Empty(Of(Run([big], "NTFS"), PreflightCode.TooLargeForFat32));
        Assert.Empty(Of(Run([big], "ReFS"), PreflightCode.TooLargeForFat32));
    }

    [Fact]
    public void NotEnoughSpace_WhenTotalExceedsFreeMinusOnePercent()
    {
        // free 1000 -> usable 990; total 995 fails, 990 passes.
        var bad = Run([File("a", 500), File("b", 495)], free: 1000);
        Assert.Single(Of(bad, PreflightCode.NotEnoughSpace));
        Assert.Empty(Of(Run([File("a", 500), File("b", 490)], free: 1000), PreflightCode.NotEnoughSpace));
    }

    [Fact]
    public void NotEnoughSpace_IgnoresFolderLengthsAndShowsReadableSizes()
    {
        var gb = 1024L * 1024 * 1024;
        var issues = Run([File("x", 146 * gb), Dir("d")], free: 120 * gb);
        var issue = Assert.Single(Of(issues, PreflightCode.NotEnoughSpace));
        Assert.Contains("146 GB", issue.Message);
        Assert.Contains("120 GB", issue.Message);
        Assert.StartsWith("Drive D:", issue.Message);
    }

    public static TheoryData<Exception> NonFatalFailures() => new()
    {
        new FileSystemException("x", IoErrorKind.NotFound, null),
        new ArgumentException("x"),
        new UnauthorizedAccessException("x"),
        new IOException("x"),
        new InvalidOperationException("x"),
    };

    [Theory]
    [MemberData(nameof(NonFatalFailures))]
    public void FreeSpaceUnknown_WhenFreeSpaceLookupFails(Exception failure)
    {
        var fs = new InMemoryFileSystem { FreeSpaceException = failure };
        var issues = new PreflightRunner(fs).Check([File("a.txt")], Dest, "NTFS", false);
        var issue = Assert.Single(issues);
        Assert.Equal(PreflightCode.FreeSpaceUnknown, issue.Code);
        Assert.Contains("D:", issue.Message);
    }

    [Fact]
    public void FreeSpaceLookup_FatalExceptionsStillPropagate()
    {
#pragma warning disable CA2201 // simulating a fatal runtime failure on purpose
        var fs = new InMemoryFileSystem { FreeSpaceException = new OutOfMemoryException() };
#pragma warning restore CA2201
        Assert.Throws<OutOfMemoryException>(() => new PreflightRunner(fs).Check([File("a.txt")], Dest, "NTFS", false));
    }

    [Fact]
    public void Messages_ArePlainLanguage()
    {
        var gb = 1024L * 1024 * 1024;
        var items = new List<WalkItem>
        {
            File(new string('a', 300)), File("CON"), File("a."), File("b:c"), File("A.txt"), File("a.txt"), File("big", 5 * gb),
        };
        var unknown = new PreflightRunner(new InMemoryFileSystem { FreeSpaceException = new IOException("x") }).Check([], Dest, "NTFS", false);
        var issues = Run(items, "SMB", free: gb).Concat(Run(items, "FAT32", free: gb)).Concat(unknown).ToList();
        Assert.Equal(Enum.GetValues<PreflightCode>().Length - 1, issues.Select(i => i.Code).Distinct().Count());
        foreach (var issue in issues)
        {
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
            foreach (var banned in new[] { "MAX_PATH", "HRESULT", "errno", "0x", "SMB", "NTFS" })
            {
                Assert.DoesNotContain(banned, issue.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Check_HundredThousandItems_IsFast()
    {
        var items = new List<WalkItem>(100_000);
        for (var d = 0; d < 1000; d++)
        {
            items.Add(Dir("folder" + d));
            for (var f = 0; f < 99; f++)
            {
                items.Add(File($@"folder{d}\file{f}.txt", 10));
            }
        }

        var runner = new PreflightRunner(new InMemoryFileSystem());
        runner.Check(items.Take(1000).ToList(), Dest, "SMB", false); // warm up
        var sw = Stopwatch.StartNew();
        var issues = runner.Check(items, Dest, "SMB", false);
        sw.Stop();
        Assert.Empty(issues);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
