using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

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

    // ---- L1: names from the source tree, and destination writes outside the destination

    [Fact]
    public async Task HostileChildName_NothingCreatedOutsideTheDestination()
    {
        AddText(@"C:\src\ok.txt", "ok");
        var ok = _rig.Mem.GetEntry(@"C:\src\ok.txt");
        _rig.Mem.InjectListingEntry(@"C:\src", ok with { FullPath = @"\\?\C:\src\..\..\escape", Attributes = FileAttributes.Directory });

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.Contains("..", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Message.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"src\a.txt", true)]
    [InlineData(@"src\a.", true)]
    [InlineData(@"src\..\..\x", false)]
    [InlineData(@"..\x", false)]
    [InlineData(@"src\..", false)]
    [InlineData("", false)]
    [InlineData(@"C:\x", false)]
    public void TreePaths_IsInside_OnlyStrictlyBelowTheRoot(string key, bool inside) =>
        Assert.Equal(inside, TreePaths.IsInside(@"\\?\D:\dst", key));

    [Fact]
    public void TreePaths_DriveRootAndShare_Resolved()
    {
        Assert.True(TreePaths.IsInside(@"\\?\D:\", "x"));
        Assert.True(TreePaths.TryContain(@"\\?\D:\", @"a\b", out var onDrive));
        Assert.Equal(@"\\?\D:\a\b", onDrive);
        Assert.True(TreePaths.TryContain(@"\\?\UNC\server\share\dst", @"a\b", out var onShare));
        Assert.Equal(@"\\?\UNC\server\share\dst\a\b", onShare);
        Assert.False(TreePaths.IsInside(@"\\?\UNC\server\share\dst", @"..\other"));
    }

    // ---- L3 and I6: which destination file a resume may treat as "this job's own"

    /// <summary>Runs a copy of a.bin and b.bin where b fails and the job stops, so it can be resumed.</summary>
    private async Task<Guid> StoppedJobAsync(ConflictRule rule)
    {
        Add(@"C:\src\a.bin", 100, seed: 1);
        Add(@"C:\src\b.bin", 100, seed: 2);
        _rig.Faults.FailRead(@"C:\src\b.bin", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop, IfExists = rule }, jobId: id);
        return id;
    }

    private void AppendToJournal(Guid id, JournalRecord record)
    {
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        journal.Append(record);
    }

    [Fact]
    public async Task Resume_ForgedPartRecordNamingAnotherFile_ThatFileIsNotReplaced()
    {
        var id = await StoppedJobAsync(ConflictRule.Skip);
        var victim = Bytes(100, seed: 9);
        _rig.Mem.AddFile(@"D:\dst\src\victim.bin", victim); // b's size, dated within the job's run
        AppendToJournal(id, new JournalRecord(
            JournalRecordKind.FilePartStarted, @"0\src\b.bin", 100, DateTime.UtcNow, null, @"src\victim.bin.0123abcd.ferrypart", @"src\victim.bin"));

        await _rig.ResumeAsync(id);

        Assert.Equal(victim, _rig.Mem.ReadAllBytes(@"D:\dst\src\victim.bin"));
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\b.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\b.bin"));
    }

    [Fact]
    public async Task Resume_RenamedCopyBlankedByPowerCut_RecognisedByItsIdentity()
    {
        var existing = Bytes(50, seed: 5);
        _rig.Mem.AddFile(@"D:\dst\src\a.bin", existing); // so a is copied as "a (2).bin"
        var id = await StoppedJobAsync(ConflictRule.Rename);
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a (2).bin"));

        // The power cut kept the rename but lost the data: the same file, now blank, and its dates tell nothing.
        await using (var blank = _rig.Mem.OpenWriteExisting(@"D:\dst\src\a (2).bin", unbuffered: false))
        {
            await blank.WriteAsync(new byte[100], 0, Ct);
        }

        var later = DateTime.UtcNow.AddHours(3);
        _rig.Mem.SetMetadata(@"D:\dst\src\a (2).bin", FileAttributes.Normal, later, later);

        await _rig.ResumeAsync(id);

        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\a.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\a (2).bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a (3).bin"));
        Assert.Equal(existing, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }

    [Fact]
    public async Task Resume_PartRecordNamingAnIdenticalFileInAnotherFolder_ThatFileIsLeftAlone()
    {
        var id = await StoppedJobAsync(ConflictRule.Skip);
        _rig.Mem.AddFile(@"D:\dst\other\same.bin", _rig.Mem.ReadAllBytes(@"C:\src\b.bin"));
        var before = _rig.Mem.GetEntry(@"D:\dst\other\same.bin");
        AppendToJournal(id, new JournalRecord(
            JournalRecordKind.FilePartStarted, @"0\src\b.bin", 100, DateTime.UtcNow, null, @"other\same.bin.0123abcd.ferrypart", @"other\same.bin"));

        await _rig.ResumeAsync(id);

        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\b.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\b.bin"));
        Assert.Equal(before, _rig.Mem.GetEntry(@"D:\dst\other\same.bin"));
    }

    // ---- L2: a resume shows the saved options that can replace, delete or reach further

    [Fact]
    public async Task Resume_ConfirmationIsShownTheSavedOptions()
    {
        var id = await StoppedJobAsync(ConflictRule.Overwrite);

        await _rig.ResumeAsync(id);

        var asked = Assert.Single(_rig.Confirmation.Asked);
        Assert.Equal(ConflictRule.Overwrite, asked.Options.IfExists);
        Assert.Equal([CopyMessages.RiskyOverwrite], asked.RiskyOptions);
    }

    [Fact]
    public void RiskyOptions_ListsEachRiskInPlainWords()
    {
        var all = new CopyOptions { IfExists = ConflictRule.OverwriteIfNewer, FollowReparsePoints = true, Verify = false, CopyPermissions = true };

        Assert.Equal(
            [CopyMessages.RiskyOverwriteIfNewer, CopyMessages.RiskyFollowLinks, CopyMessages.RiskyNoVerify, CopyMessages.RiskyPermissions],
            CopyMessages.RiskyOptions(CopyMode.Copy, all));
        Assert.Equal([CopyMessages.RiskyMove, CopyMessages.RiskyOverwriteIfNewer, CopyMessages.RiskyPermissions], CopyMessages.RiskyOptions(CopyMode.Move, all));
        Assert.Empty(CopyMessages.RiskyOptions(CopyMode.Copy, new CopyOptions()));
        Assert.True(ResumeRequest.NeedsPreview(CopyMode.Move, new CopyOptions()));
        Assert.True(ResumeRequest.NeedsPreview(CopyMode.Copy, all));
        Assert.False(ResumeRequest.NeedsPreview(CopyMode.Copy, new CopyOptions { IfExists = ConflictRule.KeepBoth }));
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
