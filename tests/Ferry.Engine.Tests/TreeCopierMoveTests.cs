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
        _rig.Mem.AddFile(@"C:\src\a.bin", a);
        Add(@"C:\src\sub\b.bin", 50);

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(2, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(a, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.True(_rig.Mem.Exists(@"C:\dst\src\sub\b.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src"));
    }

    [Fact]
    public async Task Move_SameVolume_IntoExistingFolder_RenamesEachItem()
    {
        Add(@"C:\src\a.bin", 100);
        Add(@"C:\src\sub\b.bin", 50);
        Add(@"C:\dst\src\keep.bin", 10);

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst", mode: CopyMode.Move);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.True(_rig.Mem.Exists(@"C:\dst\src\a.bin"));
        Assert.True(_rig.Mem.Exists(@"C:\dst\src\sub\b.bin"));
        Assert.True(_rig.Mem.Exists(@"C:\dst\src\keep.bin"));
        Assert.False(_rig.Mem.Exists(@"C:\src"), "Emptied source folders are removed.");
    }

    [Fact]
    public async Task Move_SameVolume_DecidedByVolumeSerial_NotDriveLetter()
    {
        Add(@"C:\src\a.bin", 100);
        var cSerial = _rig.Mem.GetEntry(@"C:\src").VolumeSerial;
        _rig.Mem.CreateDirectory(@"S:\");
        _rig.Fs.MapEntry = e => RecordingFileSystem.Plain(e.FullPath).StartsWith("S:", StringComparison.OrdinalIgnoreCase) ? e with { VolumeSerial = cSerial } : e;

        var result = await _rig.RunAsync([@"C:\src"], @"S:\dst", mode: CopyMode.Move); // S: is C: under another letter (like subst)

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
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
        var delete = log.FindIndex(l => l == @"Delete C:\src\a.bin");
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
        Assert.DoesNotContain(@"Delete C:\src\a.bin", _rig.Fs.Log); // only the attempt to remove an emptied folder, which is refused
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
