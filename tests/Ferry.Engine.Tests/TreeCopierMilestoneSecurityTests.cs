using EveryApp.Core.FileSystem;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: fixes from the M1 milestone security review (source identity, hostile names, journal failures, part identity).</summary>
public sealed class TreeCopierMilestoneSecurityTests : TreeCopierTestBase
{
    /// <summary>Progress that runs <paramref name="swap"/> once, when the job starts copying (after the walk, before the first file).</summary>
    private static ListProgress SwapWhenCopyingStarts(Action swap)
    {
        var done = 0;
        return new ListProgress
        {
            OnReport = p =>
            {
                if (p.Phase == "Copying" && Interlocked.Exchange(ref done, 1) == 0)
                {
                    swap();
                }
            },
        };
    }

    // ---- Source identity: the file opened must be the file walked

    [Fact]
    public async Task Copy_SourceSwappedAfterWalk_NotCopied()
    {
        AddText(@"C:\src\a.txt", "walked");
        var progress = SwapWhenCopyingStarts(() =>
        {
            _rig.Mem.Delete(@"C:\src\a.txt");
            AddText(@"C:\src\a.txt", "planted"); // a different file under the same name
        });

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress);

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.SourceChanged);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a.txt"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Copy_SourceOpenedOutsideItsRoot_NotCopied()
    {
        // No file ids to compare (some file systems give none): the folder is swapped for a link to a same-named file elsewhere.
        AddText(@"C:\src\sub\a.txt", "walked");
        AddText(@"C:\other\a.txt", "outside");
        _rig.Fs.MapEntry = e => e with { FileId = 0 };
        var progress = SwapWhenCopyingStarts(() =>
        {
            _rig.Mem.Delete(@"C:\src\sub\a.txt");
            _rig.Mem.Delete(@"C:\src\sub");
            _rig.Mem.AddJunction(@"C:\src\sub", @"C:\other");
        });

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress);

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.SourceChanged);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\sub\a.txt"));
    }

    [Fact]
    public async Task Move_SameVolume_SourceSwappedAfterWalk_NotRenamed()
    {
        AddText(@"C:\src\a.txt", "walked");
        _rig.Mem.CreateDirectory(@"C:\dst\src"); // so each item is renamed on its own
        var progress = SwapWhenCopyingStarts(() =>
        {
            _rig.Mem.Delete(@"C:\src\a.txt");
            AddText(@"C:\src\a.txt", "planted");
        });

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move, progress: progress);

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.SourceChanged);
        Assert.False(_rig.Mem.Exists(@"C:\dst\src\a.txt"));
        Assert.Equal("planted", Text(@"C:\src\a.txt"));
    }

    [Fact]
    public async Task Move_OtherVolume_SourceSwappedAfterWalk_NotCopiedNorDeleted()
    {
        AddText(@"C:\src\a.txt", "walked");
        var progress = SwapWhenCopyingStarts(() =>
        {
            _rig.Mem.Delete(@"C:\src\a.txt");
            AddText(@"C:\src\a.txt", "planted");
        });

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move, progress: progress);

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.SourceChanged);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a.txt"));
        Assert.Equal("planted", Text(@"C:\src\a.txt"));
    }

    [Fact]
    public async Task Copy_UnchangedSource_StillCopied()
    {
        AddText(@"C:\src\sub\a.txt", "same");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal("same", Text(@"D:\dst\src\sub\a.txt"));
    }
}
