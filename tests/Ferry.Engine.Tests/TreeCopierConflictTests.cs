using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Conflict rules.</summary>
public sealed class TreeCopierConflictTests : TreeCopierTestBase
{
    [Fact]
    public async Task Conflict_Overwrite_ReplacesExisting()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Overwrite });

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", Text(@"D:\dst\src\f.txt"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Conflict_Overwrite_ReadOnlyDestination_IsReplaced()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");
        var old = _rig.Mem.GetEntry(@"D:\dst\src\f.txt");
        _rig.Mem.SetMetadata(@"D:\dst\src\f.txt", FileAttributes.ReadOnly, old.CreationTimeUtc, old.LastWriteTimeUtc);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Overwrite });

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", Text(@"D:\dst\src\f.txt"));
        var log = _rig.Fs.Log;
        var clear = log.FindIndex(l => l == @"SetMetadata D:\dst\src\f.txt");
        var replace = log.FindIndex(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\f.txt overwrite", StringComparison.Ordinal));
        Assert.True(clear >= 0 && replace > clear, "ReadOnly is cleared on the old file right before the new one replaces it.");
        Assert.DoesNotContain(@"Delete D:\dst\src\f.txt", log);
    }

    [Fact]
    public async Task Conflict_OverwriteIfNewer_KeepsTheNewerOne()
    {
        AddText(@"C:\src\newer.txt", "src-newer");
        AddText(@"C:\src\older.txt", "src-older");
        AddText(@"D:\dst\src\newer.txt", "dst-older");
        AddText(@"D:\dst\src\older.txt", "dst-newer");
        SetTimes(@"C:\src\newer.txt", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        SetTimes(@"D:\dst\src\newer.txt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SetTimes(@"C:\src\older.txt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        SetTimes(@"D:\dst\src\older.txt", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.OverwriteIfNewer });

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("src-newer", Text(@"D:\dst\src\newer.txt"));
        Assert.Equal("dst-newer", Text(@"D:\dst\src\older.txt"));
    }

    [Fact]
    public async Task Conflict_Skip_KeepsExisting()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Skip });

        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", Text(@"D:\dst\src\f.txt"));
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task Conflict_Rename_CopiesAsNameWithNumber()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Rename });

        Assert.Equal(1, result.Copied);
        Assert.Equal("old", Text(@"D:\dst\src\f.txt"));
        Assert.Equal("new", Text(@"D:\dst\src\f (2).txt"));
    }

    [Fact]
    public async Task Conflict_Rename_SkipsNumbersAlreadyTaken()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");
        AddText(@"D:\dst\src\f (2).txt", "old2");

        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Rename });

        Assert.Equal("old2", Text(@"D:\dst\src\f (2).txt"));
        Assert.Equal("new", Text(@"D:\dst\src\f (3).txt"));
    }

    [Fact]
    public async Task Conflict_KeepBoth_RenamesExistingAndCopies()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.KeepBoth });

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", Text(@"D:\dst\src\f.txt"));
        Assert.Equal("old", Text(@"D:\dst\src\f (2).txt"));
    }

    [Fact]
    public async Task Conflict_KeepBoth_CopyFails_OldFileKeepsItsName()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");
        _rig.Faults.FailRead(@"C:\src\f.txt", 0, IoErrorKind.AccessDenied);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.KeepBoth });

        Assert.Equal(1, result.Failed);
        Assert.Equal("old", Text(@"D:\dst\src\f.txt"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f (2).txt"));
    }

    [Fact]
    public async Task Conflict_Ask_UsesResolverAnswer()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");
        _rig.Resolver.Answer = ConflictRule.Overwrite;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask });

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", Text(@"D:\dst\src\f.txt"));
        var (source, existing) = Assert.Single(_rig.Resolver.Questions);
        Assert.EndsWith(@"C:\src\f.txt", source.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"D:\dst\src\f.txt", existing.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_Ask_AnsweredAsk_IsSkip()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");
        _rig.Resolver.Answer = ConflictRule.Ask;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask });

        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", Text(@"D:\dst\src\f.txt"));
    }

    [Fact]
    public async Task Conflict_FolderInTheWay_FileFailsWithPlainMessage()
    {
        AddText(@"C:\src\f.txt", "new");
        _rig.Mem.CreateDirectory(@"D:\dst\src\f.txt");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Overwrite });

        Assert.Equal(1, result.Failed);
        Assert.Equal(CopyMessages.FolderInTheWay, Assert.Single(result.Problems).Message);
    }
}
