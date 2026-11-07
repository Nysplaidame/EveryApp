using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Basic tree copies: contents, order, links.</summary>
public sealed class TreeCopierCopyTests : TreeCopierTestBase
{
    [Fact]
    public async Task Run_CopiesTree_AllVerified()
    {
        var a = Bytes(10, 1);
        var b = Bytes(5000, 2);
        var c = Bytes(70_000, 3);
        _rig.Mem.AddFile(@"C:\src\a.txt", a);
        _rig.Mem.AddFile(@"C:\src\sub\b.bin", b);
        _rig.Mem.AddFile(@"C:\src\sub\deep\c.bin", c);
        _rig.Mem.CreateDirectory(@"C:\src\empty");
        var id = Guid.NewGuid();

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);

        Assert.Equal(3, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Problems);
        Assert.Empty(result.PreflightIssues);
        Assert.True(result.AllVerified);
        Assert.Equal(a, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.txt"));
        Assert.Equal(b, _rig.Mem.ReadAllBytes(@"D:\dst\src\sub\b.bin"));
        Assert.Equal(c, _rig.Mem.ReadAllBytes(@"D:\dst\src\sub\deep\c.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\empty"), "Empty folders are created too.");
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
        Assert.Equal(3, journal.CompletedPaths.Count);
        Assert.Contains(@"0\src\sub\deep\c.bin", journal.CompletedPaths); // keyed by source index, then the path in the destination
    }

    [Fact]
    public async Task Run_SingleFileSource_CopiesIntoDestination()
    {
        Add(@"C:\docs\report.txt", 100);

        var result = await _rig.RunAsync([@"C:\docs\report.txt"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\docs\report.txt"), _rig.Mem.ReadAllBytes(@"D:\dst\report.txt"));
    }

    [Fact]
    public async Task Run_VerifyOff_NothingReadBack_AndNotAllVerified()
    {
        Add(@"C:\src\a.bin", 1000);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { Verify = false });

        Assert.Equal(1, result.Copied);
        Assert.False(result.AllVerified);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.EndsWith(" unbuffered", StringComparison.Ordinal) && l.Contains(@"D:\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_CreatesFoldersBeforeTheirFiles_SmallFilesFirst()
    {
        Add(@"C:\src\big.bin", 3000);
        Add(@"C:\src\small.bin", 10);
        Add(@"C:\src\mid.bin", 500);
        Add(@"C:\src\sub\inner.bin", 20);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(4, result.Copied);
        var log = _rig.Fs.Log;
        int IndexOf(Func<string, bool> match) => log.FindIndex(l => match(l));
        int Commit(string name) => IndexOf(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\" + name, StringComparison.OrdinalIgnoreCase));
        var createSrc = IndexOf(l => l == @"CreateDirectory D:\dst\src");
        var createSub = IndexOf(l => l == @"CreateDirectory D:\dst\src\sub");
        Assert.True(createSrc >= 0 && createSub >= 0);
        Assert.True(createSrc < Commit("small.bin"));
        Assert.True(Commit("small.bin") < Commit("mid.bin"));
        Assert.True(Commit("mid.bin") < Commit("big.bin"));
        Assert.True(createSub < Commit(@"sub\inner.bin"));
    }

    [Fact]
    public async Task Copy_FolderLink_SkippedAndReported()
    {
        Add(@"C:\other\x.bin", 10);
        Add(@"C:\src\a.bin", 10);
        _rig.Mem.AddJunction(@"C:\src\link", @"C:\other");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\link"));
        var problem = Assert.Single(result.Problems);
        Assert.Equal(CopyMessages.LinkSkipped, problem.Message);
        Assert.Equal(IoErrorKind.None, problem.Kind);
    }

    [Fact]
    public async Task Copy_FileSymlink_CopiedAsTargetContents()
    {
        _rig.Mem.AddFile(@"C:\src\shortcut.txt", Encoding.UTF8.GetBytes("target data"), reparseTag: 0xA000000C);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal("target data", Text(@"D:\dst\src\shortcut.txt"));
        Assert.False(_rig.Mem.GetEntry(@"D:\dst\src\shortcut.txt").IsReparsePoint, "The copy is an ordinary file, not a link.");
    }
}
