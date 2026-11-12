using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Moves on one drive and across drives.</summary>
public sealed class TreeCopierMoveTests : TreeCopierTestBase
{
    [Fact]
    public async Task Move_SameVolume_UsesRenameNotCopy()
    {
        var a = Bytes(100, 1);
        var b = Bytes(50, 2);
        _rig.Mem.AddFile(@"C:\src\a.bin", a);
        _rig.Mem.AddFile(@"C:\src\sub\b.bin", b);

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(2, result.Copied);
        Assert.False(result.AllVerified, "A rename reads nothing back, so nothing was verified.");
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(a, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.Equal(b, _rig.Mem.ReadAllBytes(@"C:\dst\src\sub\b.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src"));
    }

    [Fact]
    public async Task Move_SameVolume_IntoExistingFolder_RenamesEachItem()
    {
        var a = Bytes(100, 1);
        var b = Bytes(50, 2);
        var keep = Bytes(10, 3);
        _rig.Mem.AddFile(@"C:\src\a.bin", a);
        _rig.Mem.AddFile(@"C:\src\sub\b.bin", b);
        _rig.Mem.AddFile(@"C:\dst\src\keep.bin", keep);

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(a, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.Equal(b, _rig.Mem.ReadAllBytes(@"C:\dst\src\sub\b.bin"));
        Assert.Equal(keep, _rig.Mem.ReadAllBytes(@"C:\dst\src\keep.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src"), "Emptied source folders are removed.");
    }

    // ---- moves combined with conflict rules (same drive: C: to C:, other drive: C: to D:)

    [Theory]
    [InlineData(@"C:\dst")]
    [InlineData(@"D:\dst")]
    public async Task Move_ConflictSkip_KeepsSourceAndExisting(string destination)
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(destination + @"\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], destination, new CopyOptions { IfExists = ConflictRule.Skip }, CopyMode.Move);

        Assert.Equal(1, result.Skipped);
        Assert.Equal("new", Text(@"C:\src\f.txt"));
        Assert.Equal("old", Text(destination + @"\src\f.txt"));
    }

    [Theory]
    [InlineData(@"C:\dst")]
    [InlineData(@"D:\dst")]
    public async Task Move_ConflictOverwrite_ReplacesExistingAndRemovesSource(string destination)
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(destination + @"\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], destination, new CopyOptions { IfExists = ConflictRule.Overwrite }, CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", Text(destination + @"\src\f.txt"));
        Assert.False(_rig.Mem.Exists(@"C:\src\f.txt"));
    }

    [Theory]
    [InlineData(@"C:\dst", ConflictRule.KeepBoth)]
    [InlineData(@"D:\dst", ConflictRule.KeepBoth)]
    [InlineData(@"C:\dst", ConflictRule.Rename)]
    [InlineData(@"D:\dst", ConflictRule.Rename)]
    public async Task Move_ConflictKeepBoth_ExistingUntouched_IncomingGetsNumber(string destination, ConflictRule rule)
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(destination + @"\src\f.txt", "old");
        var existingId = _rig.Mem.GetEntry(destination + @"\src\f.txt").FileId;

        var result = await _rig.RunAsync([@"C:\src"], destination, new CopyOptions { IfExists = rule }, CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal("old", Text(destination + @"\src\f.txt"));
        Assert.Equal(existingId, _rig.Mem.GetEntry(destination + @"\src\f.txt").FileId);
        Assert.Equal("new", Text(destination + @"\src\f (2).txt"));
        Assert.False(_rig.Mem.Exists(@"C:\src\f.txt"));
    }

    [Fact]
    public async Task Move_CrossVolume_FileSymlink_SkippedAndListed()
    {
        _rig.Mem.AddFile(@"C:\src\link.txt", Encoding.UTF8.GetBytes("target data"), reparseTag: 0xA000000C);
        AddText(@"C:\src\a.txt", "a");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal(CopyMessages.FileLinkSkipped, Assert.Single(result.Problems).Message);
        Assert.True(_rig.Mem.Exists(@"C:\src\link.txt"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\link.txt"));
        Assert.DoesNotContain(_rig.Fs.Log, l => l.Contains("link.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Move_SameVolume_FileSymlink_MovedAsTheLinkItself()
    {
        _rig.Mem.AddFile(@"C:\src\link.txt", Encoding.UTF8.GetBytes("target data"), reparseTag: 0xA000000C);
        _rig.Mem.CreateDirectory(@"C:\dst\src"); // item-by-item

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Contains(@"Move C:\src\link.txt -> C:\dst\src\link.txt", _rig.Fs.Log);
        Assert.True(_rig.Mem.GetEntry(@"C:\dst\src\link.txt").IsReparsePoint);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task Move_SameVolume_DecidedByVolumeId_NotDriveLetter()
    {
        Add(@"C:\src\a.bin", 100);
        _rig.Mem.VolumeIds['S'] = _rig.Mem.GetVolumeId(@"C:\")!; // S: is C: under another letter
        _rig.Mem.CreateDirectory(@"S:\");

        var result = await _rig.RunAsync([@"C:\src"], @"S:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task Move_SameSerialButOtherVolume_CopiesAndVerifies()
    {
        Add(@"C:\src\a.bin", 100);
        var cSerial = _rig.Mem.GetEntry(@"C:\src").VolumeSerial;
        _rig.Mem.CreateDirectory(@"D:\");
        _rig.Fs.MapEntry = e => RecordingFileSystem.Plain(e.FullPath).StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? e with { VolumeSerial = cSerial } : e;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move); // serials collide; volume ids do not

        Assert.Equal(1, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Equal(1, _rig.Fs.Count("CreateWrite"));
        Assert.False(_rig.Mem.Exists(@"C:\src\a.bin"));
    }

    [Fact]
    public async Task Move_RenameRefusedAsOtherDevice_FallsBackToVerifiedCopy()
    {
        var data = Bytes(300, 2);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);
        _rig.Mem.CreateDirectory(@"C:\dst\src"); // item-by-item
        _rig.Mem.OnMove = (from, to) => RecordingFileSystem.Plain(from).Equals(@"C:\src\a.bin", StringComparison.OrdinalIgnoreCase)
            ? new FileSystemException(from, IoErrorKind.NotSameDevice, null)
            : null;

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Empty(result.Problems);
        Assert.Equal(1, _rig.Fs.Count("CreateWrite"));
        Assert.Contains(_rig.Fs.Log, l => l.StartsWith(@"OpenRead C:\dst\src\a.bin.", StringComparison.Ordinal) && l.EndsWith(" unbuffered", StringComparison.Ordinal));
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src\a.bin"));
    }

    [Fact]
    public async Task Move_FolderRenameRefusedAsOtherDevice_FallsBackItemByItem()
    {
        var data = Bytes(300, 2);
        _rig.Mem.AddFile(@"C:\src\sub\a.bin", data);
        _rig.Mem.OnMove = (from, to) => RecordingFileSystem.Plain(from).Equals(@"C:\src", StringComparison.OrdinalIgnoreCase)
            ? new FileSystemException(from, IoErrorKind.NotSameDevice, null)
            : null;

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"C:\dst\src\sub\a.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src\sub\a.bin"));
    }

    [Fact]
    public async Task Move_CrossVolume_DeletesSourceOnlyAfterVerify()
    {
        var data = Bytes(5000, 7);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { Verify = false }, CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.True(result.AllVerified, "A move always verifies, even with verify off.");
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src\a.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src"));
        var log = _rig.Fs.Log;
        var verify = log.FindIndex(l => l.StartsWith(@"OpenRead D:\dst\src\a.bin.", StringComparison.Ordinal) && l.EndsWith(" unbuffered", StringComparison.Ordinal));
        var commit = log.FindIndex(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\a.bin", StringComparison.Ordinal));
        var delete = log.FindIndex(l => l == @"DeleteVerified C:\src\a.bin"); // checked and deleted through one handle
        Assert.True(verify >= 0 && verify < commit && commit < delete, string.Join(Environment.NewLine, log));
    }

    [Fact]
    public async Task Move_CrossVolume_VerifyMismatch_KeepsSource()
    {
        var data = Bytes(5000, 7);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);
        _rig.Fs.TransformWrite = (path, offset, buffer) =>
        {
            var bad = buffer.ToArray();
            bad[^1] ^= 0x01;
            return bad;
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Failed);
        Assert.False(result.AllVerified);
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"C:\src\a.bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a.bin"));
        Assert.DoesNotContain(@"DeleteVerified C:\src\a.bin", _rig.Fs.Log);
        Assert.DoesNotContain(@"Delete C:\src\a.bin", _rig.Fs.Log);
    }

    [Fact]
    public async Task Move_SameVolume_JunctionInside_MovedAsLinkNotFollowed()
    {
        Add(@"C:\other\x.bin", 10);
        Add(@"C:\src\a.bin", 10);
        _rig.Mem.AddJunction(@"C:\src\link", @"C:\other");
        _rig.Mem.CreateDirectory(@"C:\dst\src"); // forces item-by-item moves

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", new CopyOptions { FollowReparsePoints = true }, CopyMode.Move);

        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Contains(@"Move C:\src\link -> C:\dst\src\link", _rig.Fs.Log);
        Assert.True(_rig.Mem.GetEntry(@"C:\dst\src\link").IsReparsePoint);
        Assert.True(_rig.Mem.Exists(@"C:\other\x.bin"), "The link's target is never touched.");
        Assert.DoesNotContain(_rig.Fs.Log, l => l.Contains(@"C:\other", StringComparison.OrdinalIgnoreCase) || l.Contains(@"link\x.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.Copied);
    }

    [Fact]
    public async Task Move_CrossVolume_JunctionInside_LeftInPlaceWithProblem()
    {
        Add(@"C:\other\x.bin", 10);
        Add(@"C:\src\a.bin", 10);
        _rig.Mem.AddJunction(@"C:\src\link", @"C:\other");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { FollowReparsePoints = true }, CopyMode.Move);

        Assert.Equal(1, result.Copied);
        Assert.Equal(CopyMessages.LinkNotMovedAcrossDrives, Assert.Single(result.Problems).Message);
        Assert.True(_rig.Mem.Exists(@"C:\src\link"));
        Assert.True(_rig.Mem.Exists(@"C:\other\x.bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\link"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a.bin"));
    }
}
