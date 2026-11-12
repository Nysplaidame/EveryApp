using System.Diagnostics;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Paths;

public sealed class TreeWalkerTests
{
    private static readonly byte[] One = [1];

    private static List<string> RelPaths(IEnumerable<WalkItem> items) => items.Select(i => i.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public void Walk_YieldsFilesAndFoldersWithRelativePaths()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a.txt", One);
        fs.AddFile(@"C:\r\sub\b.txt", One);
        fs.AddFile(@"C:\r\sub\foo.", One);
        fs.CreateDirectory(@"C:\r\empty");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["a.txt", "empty", "sub", @"sub\b.txt", @"sub\foo."], RelPaths(items));
        Assert.Empty(skips);
        Assert.All(items, i => Assert.StartsWith(@"\\?\C:\r\", i.Entry.FullPath));
    }

    [Fact]
    public void Walk_RootGivenInDifferentForms_SameRelativePaths()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\sub\b.txt", One);
        foreach (var root in new[] { @"C:\r\", @"c:/R", @"\\?\C:\r" })
        {
            Assert.Equal(["sub", @"sub\b.txt"], RelPaths(new TreeWalker(fs, false, []).Walk(root, _ => { })));
        }
    }

    [Fact]
    public void Walk_SkipsReparsePointsByDefault()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\real\a.txt", One);
        fs.AddJunction(@"C:\r\link", @"C:\r\real");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["real", @"real\a.txt"], RelPaths(items));
        var skip = Assert.Single(skips);
        Assert.Contains("link", skip.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\r\link", skip.Path);
    }

    [Fact]
    public void Walk_FollowingReparsePoints_BreaksLoops()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a.txt", One);
        fs.AddJunction(@"C:\r\loop", @"C:\r");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, true, []).Walk(@"C:\r", skips.Add).ToList();

        // loop is entered once; its own child "loop" (same id) is then recognised as already visited, reported and not listed.
        Assert.Equal(["a.txt"], RelPaths(items));
        var skip = Assert.Single(skips);
        Assert.Contains("already scanned", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Walk_FollowingReparsePoints_DescendsIntoNonLoopingLink()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\other\x.txt", One);
        fs.AddFile(@"C:\r\a.txt", One);
        fs.AddJunction(@"C:\r\link", @"C:\other");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, true, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["a.txt", "link", @"link\x.txt"], RelPaths(items));
        Assert.Empty(skips);
    }

    [Fact]
    public void Walk_ExcludeGlobs_Applied()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\keep.txt", One);
        fs.AddFile(@"C:\r\Thumbs.db", One);
        fs.AddFile(@"C:\r\sub\thumbs.DB", One);
        fs.AddFile(@"C:\r\sub\keep2.txt", One);
        fs.AddFile(@"C:\r\app\node_modules\pkg\index.js", One);
        fs.AddFile(@"C:\r\node_modules\z.js", One);
        fs.AddFile(@"C:\r\app\main.js", One);

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, ["Thumbs.db", "**/node_modules/**"]).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["app", @"app\main.js", "keep.txt", "sub", @"sub\keep2.txt"], RelPaths(items));
        Assert.Empty(skips); // exclusions are a choice, not a problem
    }

    [Fact]
    public void Walk_ExcludedFolder_IsNotEnumerated()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\node_modules\z.js", One);
        fs.DenyListing(@"C:\r\node_modules\locked");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, ["**/node_modules/**"]).Walk(@"C:\r", skips.Add).ToList();

        Assert.Empty(items);
        Assert.Empty(skips);
    }

    [Fact]
    public void Walk_AccessDeniedFolder_ReportsAndContinues()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a\1.txt", One);
        fs.DenyListing(@"C:\r\b");
        fs.AddFile(@"C:\r\c\3.txt", One);

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["a", @"a\1.txt", "b", "c", @"c\3.txt"], RelPaths(items));
        var skip = Assert.Single(skips);
        Assert.Equal(@"C:\r\b", skip.Path);
        Assert.DoesNotContain(@"\\?\", skip.Path, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(skip.Reason));
    }

    [Fact]
    public void Walk_MissingRoot_ReportsInsteadOfThrowing()
    {
        var skips = new List<WalkSkip>();
        var items = new TreeWalker(new InMemoryFileSystem(), false, []).Walk(@"C:\nope", skips.Add).ToList();

        Assert.Empty(items);
        Assert.Single(skips);
    }

    [Fact]
    public void Walk_RootIsAFile_ReportsInsteadOfWalking()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a.txt", One);
        var skips = new List<WalkSkip>();

        Assert.Empty(new TreeWalker(fs, false, []).Walk(@"C:\r\a.txt", skips.Add).ToList());
        Assert.Single(skips);
    }

    [Fact]
    public void Walk_DeepTree_DoesNotOverflowTheStack()
    {
        var fs = new InMemoryFileSystem();
        var path = @"C:\r";
        for (var i = 0; i < 3000; i++)
        {
            path += @"\d";
        }

        fs.CreateDirectory(path);
        fs.AddFile(path + @"\leaf.txt", One);

        var count = new TreeWalker(fs, false, []).Walk(@"C:\r", _ => { }).Count();

        Assert.Equal(3001, count);
    }

    [Fact]
    public void Walk_IsLazy()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a\1.txt", One);
        fs.DenyListing(@"C:\r\z");
        var skips = new List<WalkSkip>();

        using var e = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).GetEnumerator();
        Assert.True(e.MoveNext());
        Assert.Empty(skips); // the locked folder has not been reached yet
    }

    private const uint Cloud6 = 0x9000601A;
    private const uint Dedup = 0x80000013;
    private const uint SymlinkTag = 0xA000000C;
    private const uint MountPointTag = 0xA0000003;

    [Fact]
    public void Walk_CloudTaggedFolder_IsWalkedNormally()
    {
        var fs = new InMemoryFileSystem();
        fs.AddReparseDirectory(@"C:\r\OneDrive", Cloud6);
        fs.AddFile(@"C:\r\OneDrive\doc.txt", One, Cloud6);

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["OneDrive", @"OneDrive\doc.txt"], RelPaths(items));
        Assert.Empty(skips);
        Assert.Equal(Cloud6, items.Single(i => i.RelativePath == "OneDrive").Entry.ReparseTag);
    }

    [Fact]
    public void Walk_DedupAndCloudFiles_AreYielded()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\dedup.bin", One, Dedup);
        fs.AddFile(@"C:\r\cloud.bin", One, Cloud6);

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["cloud.bin", "dedup.bin"], RelPaths(items));
        Assert.Empty(skips);
    }

    [Theory]
    [InlineData(SymlinkTag)]
    [InlineData(MountPointTag)]
    [InlineData(0x20000099u)] // unknown name surrogate
    public void Walk_NameSurrogateFolder_IsSkippedAsLink(uint tag)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\other\x.txt", One);
        fs.AddReparseDirectory(@"C:\r\l", tag, @"C:\other");

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Empty(items);
        Assert.Contains("link", Assert.Single(skips).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Walk_FileSymlink_IsYieldedWithItsTag()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\s.txt", One, SymlinkTag);

        var skips = new List<WalkSkip>();
        var item = Assert.Single(new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add));

        Assert.Equal("s.txt", item.RelativePath);
        Assert.Equal(SymlinkTag, item.Entry.ReparseTag);
        Assert.Empty(skips);
    }

    [Fact]
    public void Walk_FollowingLinks_JunctionToWalkedSibling_YieldsEachFileOnce()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\sibling\one.txt", One);
        fs.AddFile(@"C:\r\sibling\deep\two.txt", One);
        fs.AddJunction(@"C:\r\a_link", @"C:\r\sibling"); // visited before "sibling" itself
        fs.AddJunction(@"C:\r\z_link", @"C:\r\sibling"); // visited after

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, true, []).Walk(@"C:\r", skips.Add).ToList();

        var fileIds = items.Where(i => !i.Entry.Attributes.HasFlag(FileAttributes.Directory)).Select(i => i.Entry.FileId).ToList();
        Assert.Equal(2, fileIds.Count);
        Assert.Equal(2, fileIds.Distinct().Count());
        Assert.Equal(2, skips.Count);
        Assert.All(skips, s => Assert.Contains("already scanned", s.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Walk_FollowingLinks_RootReachedThroughLink_IsWalkedOnce()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\real\f.txt", One);
        fs.AddJunction(@"C:\real\j", @"C:\real");

        var items = new TreeWalker(fs, true, []).Walk(@"C:\real", _ => { }).ToList();

        Assert.Equal(["f.txt"], RelPaths(items));
    }

    [Theory]
    [InlineData("_*")]
    [InlineData("*_")]
    [InlineData("?")]
    [InlineData("_")]
    public void Walk_ShortGlobs_DoNotPruneUnrelatedFolders(string glob)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\photos\a.jpg", One);
        fs.AddFile(@"C:\r\photos\sub\b.jpg", One);

        var items = new TreeWalker(fs, false, [glob]).Walk(@"C:\r", _ => { }).ToList();

        Assert.Equal(["photos", @"photos\a.jpg", @"photos\sub", @"photos\sub\b.jpg"], RelPaths(items));
    }

    [Fact]
    public void Walk_NodeModulesGlob_PrunesAtAnyDepth_AndThumbsOnlyExcludesThatFile()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\node_modules\a.js", One);
        fs.AddFile(@"C:\r\x\y\node_modules\b.js", One);
        fs.AddFile(@"C:\r\x\y\node_modules_not\c.js", One);
        fs.AddFile(@"C:\r\x\Thumbs.db", One);
        fs.AddFile(@"C:\r\x\Thumbs.db.txt", One);

        var items = new TreeWalker(fs, false, ["**/node_modules/**", "Thumbs.db"]).Walk(@"C:\r", _ => { }).ToList();

        Assert.Equal(["x", @"x\Thumbs.db.txt", @"x\y", @"x\y\node_modules_not", @"x\y\node_modules_not\c.js"], RelPaths(items));
    }

    [Fact]
    public void Walk_ChildOutsideRootBySharedPrefix_IsRejected()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\ok.txt", One);
        fs.AddFile(@"C:\rx\y", One);
        fs.InjectListingEntry(@"C:\r", fs.GetEntry(@"C:\rx\y"));

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"\\?\C:\r", skips.Add).ToList();

        Assert.Equal(["ok.txt"], RelPaths(items));
        Assert.Contains("outside", Assert.Single(skips).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Walk_ListingFailsPartway_ReportsAndContinuesWithOtherFolders()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(@"C:\r\a\1.txt", One);
        fs.AddFile(@"C:\r\b\2.txt", One);
        fs.AddFile(@"C:\r\b\3.txt", One);
        fs.AddFile(@"C:\r\c\4.txt", One);
        fs.FailListingAfter(@"C:\r\b", 1);

        var skips = new List<WalkSkip>();
        var items = new TreeWalker(fs, false, []).Walk(@"C:\r", skips.Add).ToList();

        Assert.Equal(["a", @"a\1.txt", "b", @"b\2.txt", "c", @"c\4.txt"], RelPaths(items));
        Assert.Equal(@"C:\r\b", Assert.Single(skips).Path);
    }

    [Fact]
    public void Enumerate_JunctionLoop_SkipsAndReports()
    {
        var temp = Path.Combine(Path.GetTempPath(), "everyapp-walk-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(temp, "parent");
        var junction = Path.Combine(parent, "back");
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, "file.txt"), One);
        try
        {
            MakeJunction(junction, parent);

            var skips = new List<WalkSkip>();
            var items = new TreeWalker(new WindowsFileSystem(), false, []).Walk(parent, skips.Add).ToList();

            Assert.Equal(["file.txt"], RelPaths(items));
            var skip = Assert.Single(skips);
            Assert.Contains("link", skip.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(junction, skip.Path);

            // Even when following links the walk must end (the visited-set stops the loop).
            var followed = new TreeWalker(new WindowsFileSystem(), true, []).Walk(parent, _ => { }).ToList();
            Assert.Equal(["file.txt"], RelPaths(followed));

            var entry = new WindowsFileSystem().GetEntry(junction);
            Assert.True(entry.IsReparsePoint);
            Assert.Equal(0xA0000003u, entry.ReparseTag);

            // Listing reports the same tag, and following the link describes the folder it leads to.
            var listed = new WindowsFileSystem().EnumerateChildren(parent).Single(e => e.IsReparsePoint);
            Assert.Equal(0xA0000003u, listed.ReparseTag);
            var target = new WindowsFileSystem().GetEntryFollowingLinks(junction);
            var parentEntry = new WindowsFileSystem().GetEntry(parent);
            Assert.Equal((parentEntry.VolumeSerial, parentEntry.FileId), (target.VolumeSerial, target.FileId));
            Assert.Equal(0u, new WindowsFileSystem().GetEntry(Path.Combine(parent, "file.txt")).ReparseTag);
        }
        finally
        {
            // Remove the junction itself first, never recurse through it.
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }

            Directory.Delete(temp, recursive: true);
        }
    }

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
