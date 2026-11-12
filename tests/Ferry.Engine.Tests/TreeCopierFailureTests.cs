using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Failure rules and verification.</summary>
public sealed class TreeCopierFailureTests : TreeCopierTestBase
{
    [Fact]
    public async Task Failure_RetryThenSkip_ListsProblemAndContinues()
    {
        for (var i = 0; i < 5; i++)
        {
            Add($@"C:\src\f{i}.bin", 100 + i, seed: i);
        }

        _rig.Faults.FailRead(@"C:\src\f2.bin", 0, IoErrorKind.BadBlock);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.RetryThenSkipAndList });

        Assert.Equal(4, result.Copied);
        Assert.Equal(1, result.Failed);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("f2.bin", problem.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(IoErrorKind.BadBlock, problem.Kind);
        Assert.Equal(CopyMessages.CouldNotRead, problem.Message);
        Assert.Equal(3, _rig.Fs.Log.Count(l => l == @"OpenRead C:\src\f2.bin")); // one try and two retries
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f2.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\f4.bin"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Failure_NotRetried_WhenRetryCannotHelp()
    {
        Add(@"C:\src\f.bin", 100);
        _rig.Faults.FailRead(@"C:\src\f.bin", 0, IoErrorKind.AccessDenied);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, _rig.Fs.Log.Count(l => l == @"OpenRead C:\src\f.bin"));
    }

    [Fact]
    public async Task Failure_Stop_StopsAndKeepsCompleted()
    {
        for (var i = 0; i < 5; i++)
        {
            Add($@"C:\src\f{i}.bin", 10 + i, seed: i); // sizes rise with i, so small-first keeps this order
        }

        _rig.Faults.FailRead(@"C:\src\f2.bin", 0, IoErrorKind.BadBlock);
        var id = Guid.NewGuid();

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);

        Assert.True(result.Stopped);
        Assert.Equal(2, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\f0.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\f1.bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f2.bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f3.bin"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f4.bin"));
        Assert.Contains(result.Problems, p => p.Message == CopyMessages.StoppedAfterProblem);
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.False(journal.IsFinished); // a stopped job can be resumed
    }

    [Fact]
    public async Task Copy_DiskFullMidFile_RemovesPartFileAndReports()
    {
        Add(@"C:\src\big.bin", 40_000);
        _rig.Fs.OnWrite = (path, offset) => offset > 0 && IsPartOf(path, "big.bin") ? new FileSystemException(path, IoErrorKind.DiskFull, null) : null;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { BufferSizeBytes = 4096 });

        Assert.Equal(1, result.Failed);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(IoErrorKind.DiskFull, problem.Kind);
        Assert.Equal(CopyMessages.NotEnoughSpace("D:"), problem.Message);
        Assert.Contains("There isn't enough space on D:", problem.Message, StringComparison.Ordinal);
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\big.bin"));
        Assert.Equal(1, _rig.Fs.Count("CreateWrite")); // a full disk is not retried
    }

    [Fact]
    public async Task Verify_CorruptedDestination_Detected()
    {
        Add(@"C:\src\a.bin", 3000, seed: 1);
        Add(@"C:\src\b.bin", 4000, seed: 2);
        _rig.Fs.TransformWrite = (path, offset, data) =>
        {
            if (!IsPartOf(path, "b.bin") || offset != 0)
            {
                return data;
            }

            var bad = data.ToArray();
            bad[0] ^= 0xFF; // the disk stores a different byte than it was given
            return bad;
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.False(result.AllVerified);
        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Failed);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("b.bin", problem.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CopyMessages.VerifyMismatch, problem.Message);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\b.bin"), "A copy that failed the check never takes the real name.");
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a.bin"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Verify_Mismatch_WhenReplacing_OldDestinationIsKept()
    {
        AddText(@"C:\src\f.txt", "new data");
        AddText(@"D:\dst\src\f.txt", "old data");
        _rig.Fs.TransformWrite = (path, offset, data) =>
        {
            var bad = data.ToArray();
            bad[0] ^= 0xFF;
            return bad;
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Overwrite });

        Assert.Equal(1, result.Failed);
        Assert.Equal("old data", Text(@"D:\dst\src\f.txt"));
    }

    [Fact]
    public async Task Verify_CouldNotReadBack_NotAllVerified()
    {
        Add(@"C:\src\a.bin", 300, seed: 1);
        Add(@"C:\src\b.bin", 400, seed: 2);
        _rig.Fs.OnOpenRead = (path, unbuffered) =>
            unbuffered && IsPartOf(path, "b.bin") ? new FileSystemException(path, IoErrorKind.BadBlock, null) : null;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Failed);
        Assert.Equal(CopyMessages.VerifyCouldNotRead, Assert.Single(result.Problems).Message);
        Assert.False(result.AllVerified);
    }

    [Fact]
    public async Task Verify_NothingCopied_NotAllVerified()
    {
        AddText(@"C:\src\f.txt", "new");
        AddText(@"D:\dst\src\f.txt", "old");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Skip });

        Assert.Equal(1, result.Skipped);
        Assert.False(result.AllVerified, "Nothing was read back, so nothing was verified.");
    }

    [Fact]
    public async Task PartRecordRefused_FileFails_AndNoPartFileIsWritten()
    {
        AddText(@"C:\src\ok.txt", "ok");
        AddText(@"C:\src\bad:name.txt", "x"); // the journal refuses this name (':'), so its part file must never be created

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Failed);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(CopyMessages.NotRecorded, problem.Message);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("CreateWrite ", StringComparison.Ordinal) && l.Contains("bad:name", StringComparison.Ordinal));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Verify_ReadsDestinationUnbuffered()
    {
        Add(@"C:\src\a.bin", 3000);

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Contains(_rig.Fs.Log, l => l.StartsWith(@"OpenRead D:\dst\src\a.bin.", StringComparison.Ordinal) && l.EndsWith(".ferrypart unbuffered", StringComparison.Ordinal));
    }
}
