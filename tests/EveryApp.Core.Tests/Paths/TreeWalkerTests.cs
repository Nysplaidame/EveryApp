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
        Assert.Equal(["a.txt", "loop", @"loop\a.txt"], RelPaths(items));
        var skip = Assert.Single(skips);
        Assert.Contains("loop", skip.Reason, StringComparison.OrdinalIgnoreCase);
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
            var followed = new TreeWalker(new WindowsFileSystem(), true, []).Walk(parent, _ => { }).Take(1000).ToList();
            Assert.True(followed.Count < 1000);
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
