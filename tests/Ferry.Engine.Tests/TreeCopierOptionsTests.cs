using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Checksums, permissions, extra data streams and block cloning.</summary>
public sealed class TreeCopierOptionsTests : TreeCopierTestBase
{
    [Fact]
    public async Task ExportChecksums_WritesOneLinePerFile_MatchingHashes()
    {
        AddText(@"C:\src\a.txt", "alpha");
        AddText(@"C:\src\sub\b.txt", "beta");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { ExportChecksums = true, HashAlgorithm = HashAlgorithmKind.Sha256 });

        Assert.Equal(2, result.Copied);
        var lines = Text(@"D:\dst\ferry-checksums.sha256").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        Assert.Contains($@"{Sha("alpha")}  src\a.txt", lines);
        Assert.Contains($@"{Sha("beta")}  src\sub\b.txt", lines);
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task CopyPermissions_On_CopiesSecurityInTheFake()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.SetSecurity(@"C:\src\a.txt", "owner-only");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Equal(1, result.Copied);
        Assert.Equal("owner-only", _rig.Mem.GetSecurity(@"D:\dst\src\a.txt"));
    }

    [Fact]
    public async Task CopyPermissions_NoPrivilege_ReportsNeedsAdministrator()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.CopySecurityException = new FileSystemException(@"D:\dst\src\a.txt", IoErrorKind.AccessDenied, null);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Equal(1, result.Copied); // the data is safe; only the permissions are missing
        Assert.Equal(CopyMessages.NeedsAdministrator, Assert.Single(result.Problems).Message);
    }

    [Fact]
    public async Task AlternateStreams_OptIn_CopiedOntoPartBeforeRename()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.AddStream(@"C:\src\a.txt", ":Zone.Identifier:$DATA", [1, 2, 3]);

        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyAlternateStreams = true });

        Assert.Equal([1, 2, 3], _rig.Mem.GetStreams(@"D:\dst\src\a.txt")[":Zone.Identifier:$DATA"]);
        var log = _rig.Fs.Log;
        var streams = log.FindIndex(l => l.StartsWith("CopyStreams ", StringComparison.Ordinal) && l.EndsWith(FileCopier.PartSuffix, StringComparison.Ordinal));
        var commit = log.FindIndex(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\a.txt", StringComparison.Ordinal));
        Assert.True(streams >= 0 && streams < commit);
    }

    [Fact]
    public async Task AlternateStreams_Off_NotCopied()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.AddStream(@"C:\src\a.txt", ":Zone.Identifier:$DATA", [1, 2, 3]);

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Empty(_rig.Mem.GetStreams(@"D:\dst\src\a.txt"));
        Assert.Equal(0, _rig.Fs.Count("CopyStreams"));
    }

    [Fact]
    public async Task BlockClone_ReFSSameVolume_ClonesInsteadOfWriting()
    {
        var data = Bytes(10_000, 3);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);
        _rig.Mem.CloneSupported = true;
        _rig.Destination.FileSystem = "ReFS";

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst");

        Assert.Equal(1, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Contains(_rig.Fs.Log, l => l.StartsWith("Clone ", StringComparison.Ordinal) && l.EndsWith(" ok", StringComparison.Ordinal));
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.Empty(_rig.PartFiles(@"C:\dst"));
    }

    [Fact]
    public async Task BlockClone_NotTried_AcrossVolumesOrOnNtfs()
    {
        Add(@"C:\src\a.bin", 1000);
        Add(@"C:\src2\b.bin", 1000);
        _rig.Mem.CloneSupported = true;
        _rig.Destination.FileSystem = "ReFS";
        await _rig.RunAsync([@"C:\src"], @"D:\dst");
        _rig.Destination.FileSystem = "NTFS";
        await _rig.RunAsync([@"C:\src2"], @"C:\dst");

        Assert.Equal(0, _rig.Fs.Count("Clone"));
        Assert.Equal(2, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task BlockClone_Fails_FallsBackToNormalCopy()
    {
        var data = Bytes(1000, 4);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);
        _rig.Destination.FileSystem = "ReFS"; // but the fake refuses to clone

        var result = await _rig.RunAsync([@"C:\src"], @"C:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Contains(_rig.Fs.Log, l => l.StartsWith("Clone ", StringComparison.Ordinal) && l.EndsWith(" no", StringComparison.Ordinal));
        Assert.Equal(1, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"C:\dst\src\a.bin"));
    }
}
