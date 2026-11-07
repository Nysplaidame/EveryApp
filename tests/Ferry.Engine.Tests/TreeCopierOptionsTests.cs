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
    public async Task CopyPermissions_OwnerNotKept_PlainWarningOncePerJob()
    {
        AddText(@"C:\src\a.txt", "a");
        AddText(@"C:\src\b.txt", "b");
        _rig.Mem.CopySecurityResult = SecurityCopyResult.OwnerNotKept;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Equal(2, result.Copied); // the data is safe; only the owner is missing
        Assert.Equal(CopyMessages.OwnerNotKept, Assert.Single(result.Problems).Message);
    }

    [Fact]
    public async Task CopyPermissions_Failure_PlainWarning_NeverAdvisesElevating()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.CopySecurityException = new FileSystemException(@"D:\dst\src\a.txt", IoErrorKind.AccessDenied, null);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Equal(1, result.Copied);
        Assert.NotEmpty(result.Problems); // the file and the folder the job created
        Assert.All(result.Problems, p => Assert.Equal(CopyMessages.PermissionsNotCopied, p.Message));
    }

    [Fact]
    public async Task CopyPermissions_WouldLockOut_RevertedWithWarning()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.CopySecurityResult = SecurityCopyResult.Reverted;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Contains(result.Problems, p => p.Path.EndsWith(@"D:\dst\src\a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Problems, p => Assert.Equal(CopyMessages.PermissionsReverted, p.Message));
    }

    [Fact]
    public async Task CopyPermissions_NotBetweenFixedLocalDrives_Skipped()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Destination.FixedLocal = false;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyPermissions = true });

        Assert.Equal(1, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CopySecurity"));
        Assert.Equal(CopyMessages.PermissionsNotLocal, Assert.Single(result.Problems).Message);
    }

    [Fact]
    public async Task AlternateStreams_OptIn_CopiedOntoPartBeforeRename()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.AddStream(@"C:\src\a.txt", ":Zone.Identifier:$DATA", [1, 2, 3]);

        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyAlternateStreams = true });

        Assert.Equal([1, 2, 3], _rig.Mem.GetStreams(@"D:\dst\src\a.txt")[":Zone.Identifier:$DATA"]);
        var log = _rig.Fs.Log;
        var streams = log.FindIndex(l => l.StartsWith("CopyStreams ", StringComparison.Ordinal) && l.EndsWith(FileCopier.PartSuffix + " all", StringComparison.Ordinal));
        var commit = log.FindIndex(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\a.txt", StringComparison.Ordinal));
        Assert.True(streams >= 0 && streams < commit);
    }

    [Fact]
    public async Task AlternateStreams_Off_OnlyTheInternetMarkerIsCopied()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.AddStream(@"C:\src\a.txt", ":Zone.Identifier:$DATA", [1, 2, 3]);
        _rig.Mem.AddStream(@"C:\src\a.txt", ":other:$DATA", [4]);
        _rig.Mem.AddStream(@"C:\src\a.txt", ":bad:name:$DATA", [5]);

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        var streams = _rig.Mem.GetStreams(@"D:\dst\src\a.txt");
        Assert.Equal([1, 2, 3], streams[":Zone.Identifier:$DATA"]);
        Assert.Single(streams);
    }

    [Fact]
    public async Task AlternateStreams_OptIn_OnlyWellFormedNames()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.AddStream(@"C:\src\a.txt", ":other:$DATA", [4]);
        _rig.Mem.AddStream(@"C:\src\a.txt", ":bad:name:$DATA", [5]);

        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { CopyAlternateStreams = true });

        Assert.Equal([":other:$DATA"], _rig.Mem.GetStreams(@"D:\dst\src\a.txt").Keys);
    }

    [Fact]
    public async Task AlternateStreams_MoveAndStreamCopyFails_FileFailsAndSourceKept()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.CopyStreamsException = new FileSystemException(@"D:\x", IoErrorKind.AccessDenied, null);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move);

        Assert.Equal(1, result.Failed);
        Assert.Equal(CopyMessages.StreamsNotCopiedOriginalKept, Assert.Single(result.Problems).Message);
        Assert.True(_rig.Mem.Exists(@"C:\src\a.txt"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a.txt"));
    }

    [Fact]
    public async Task AlternateStreams_CopyAndStreamCopyFails_Warning()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Mem.CopyStreamsException = new FileSystemException(@"D:\x", IoErrorKind.AccessDenied, null);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal(CopyMessages.StreamsNotCopied, Assert.Single(result.Problems).Message);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    public async Task InternetMarker_DriveCannotKeepIt_OneJobWarning(string fileSystem)
    {
        AddText(@"C:\src\a.txt", "a");
        AddText(@"C:\src\b.txt", "b");
        _rig.Destination.FileSystem = fileSystem;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(2, result.Copied);
        var issue = Assert.Single(result.PreflightIssues);
        Assert.Equal(PreflightCode.ZoneMarkerNotKept, issue.Code);
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
