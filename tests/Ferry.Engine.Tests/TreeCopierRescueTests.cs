using EveryApp.Core.FileSystem;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: rescue mode and network retry at job level.</summary>
public sealed class TreeCopierRescueTests : TreeCopierTestBase
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task Move_RescuedFile_KeepsTheSource()
    {
        Add(@"C:\src\a.bin", MiB, seed: 1);
        Add(@"C:\src\b.bin", 1000, seed: 2);
        _rig.Faults.FailReadRange(@"C:\src\a.bin", 8192, 4096, IoErrorKind.BadBlock);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { RescueMode = true }, CopyMode.Move);

        Assert.Equal(2, result.Copied);
        Assert.Equal(0, result.Failed);
        Assert.True(_rig.Mem.Exists(@"C:\src\a.bin"), "a rescued file's original must never be removed");
        Assert.False(_rig.Mem.Exists(@"C:\src\b.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a.bin.ferry-lost.txt"));
        Assert.False(result.AllVerified);
        Assert.Contains(result.Problems, p => p.Message.StartsWith("Part of this file couldn't be read and was filled with blanks (4 KB lost)", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.RescuedOriginalKept);
    }

    [Fact]
    public async Task Copy_RescuedFile_JobIsNotAllVerified()
    {
        Add(@"C:\src\a.bin", MiB, seed: 3);
        Add(@"C:\src\b.bin", 1000, seed: 4);
        _rig.Faults.FailReadRange(@"C:\src\a.bin", 8192, 4096, IoErrorKind.BadBlock);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { RescueMode = true });

        Assert.Equal(2, result.Copied);
        Assert.False(result.AllVerified);
        var expected = _rig.Mem.ReadAllBytes(@"C:\src\a.bin");
        Array.Clear(expected, 8192, 4096);
        Assert.Equal(expected, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }

    [Fact]
    public async Task Copy_RescueTooMuchLost_FailsWithoutWholeFileRetries()
    {
        Add(@"C:\src\a.bin", 3 * MiB, seed: 5);
        _rig.Mem.SectorSize = 512;
        _rig.Faults.FailReadRange(@"C:\src\a.bin", 0, 3 * MiB, IoErrorKind.BadBlock);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { RescueMode = true });

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.TooMuchUnreadable);
        Assert.Equal(1, _rig.Faults.OpenedReads);
    }

    [Fact]
    public async Task Copy_NetworkGaveUp_FailsWithoutWholeFileRetries()
    {
        Add(@"C:\src\a.bin", 1000, seed: 6);
        _rig.Faults.FailWriteWhere(p => IsPartOf(p, "a.bin"), 0, IoErrorKind.NetworkLost);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Problems, p => p.Message == "The network connection was lost, even after waiting 5 minutes");
        Assert.Equal(1, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task Copy_NetworkDropsBriefly_JobCompletesVerified()
    {
        Add(@"C:\src\a.bin", 3 * MiB, seed: 7);
        _rig.Faults.FailWriteWhere(p => IsPartOf(p, "a.bin"), MiB, IoErrorKind.NetworkLost, times: 2, handleDies: true);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { BufferSizeBytes = MiB });

        Assert.Equal(1, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\a.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }
}
