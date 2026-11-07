using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Resume and journaled part files.</summary>
public sealed class TreeCopierResumeTests : TreeCopierTestBase
{
    [Fact]
    public async Task Resume_SkipsCompletedFiles()
    {
        for (var i = 0; i < 5; i++)
        {
            Add($@"C:\src\f{i}.bin", 10 + i, seed: i);
        }

        _rig.Faults.FailRead(@"C:\src\f2.bin", 0, IoErrorKind.SharingViolation, times: 3);
        var id = Guid.NewGuid();
        var first = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);
        Assert.Equal(2, first.Copied);
        _rig.Fs.ClearLog();

        var resumed = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);

        Assert.Equal(3, resumed.Copied);
        Assert.Equal(0, resumed.Failed);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith(@"CreateWrite D:\dst\src\f0.bin", StringComparison.Ordinal) || l.StartsWith(@"CreateWrite D:\dst\src\f1.bin", StringComparison.Ordinal));
        Assert.Equal(3, _rig.Fs.Count("CreateWrite"));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(_rig.Mem.ReadAllBytes($@"C:\src\f{i}.bin"), _rig.Mem.ReadAllBytes($@"D:\dst\src\f{i}.bin"));
        }

        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
    }

    [Fact]
    public async Task Resume_FinishedJob_DoesNothing()
    {
        Add(@"C:\src\a.bin", 10);
        var id = Guid.NewGuid();
        await _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);
        _rig.Fs.ClearLog();

        var again = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);

        Assert.Equal(0, again.Copied);
        Assert.Empty(_rig.Fs.Log);
    }

    [Fact]
    public async Task Run_JournalsPartPathBeforeWriting()
    {
        Add(@"C:\src\a.bin", 100);
        var id = Guid.NewGuid();
        var journalHadIt = false;
        _rig.Fs.AfterCreateWrite = path =>
        {
            using var stream = new FileStream(Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            journalHadIt = reader.ReadToEnd().Contains(Path.GetFileName(RecordingFileSystem.Plain(path)), StringComparison.Ordinal);
        };

        await _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);

        Assert.True(journalHadIt, "The part file's name reached the journal file before the part file existed.");
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        var part = Assert.Single(journal.PartFiles);
        Assert.Equal(@"src\a.bin", part.RelativePath);
        Assert.StartsWith(@"src\a.bin.", part.PartRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_DeletesOnlyTheJournaledPartFiles()
    {
        Add(@"C:\src\a.bin", 100);
        Add(@"C:\src\b.bin", 200);
        AddText(@"D:\dst\src\other.bin.deadbeef.ferrypart", "not ours"); // someone else's part file: never touched
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _rig.Fs.AfterCreateWrite = path =>
        {
            if (IsPartOf(path, "b.bin"))
            {
                cts.Cancel();
            }
        };
        _rig.Fs.OnDelete = path => IsPartOf(path, "b.bin") ? new SimulatedCrashException() : null; // the process "dies" before cleaning up
        var id = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id, ct: cts.Token));
        var leftover = Assert.Single(_rig.PartFiles(@"D:\dst"), p => IsPartOf(p, "b.bin"));
        _rig.Fs.AfterCreateWrite = null;
        _rig.Fs.OnDelete = null;

        var resumed = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);

        Assert.Equal(1, resumed.Copied);
        Assert.False(_rig.Mem.Exists(leftover));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\other.bin.deadbeef.ferrypart"));
        Assert.Equal("not ours", Text(@"D:\dst\src\other.bin.deadbeef.ferrypart"));
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\b.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\b.bin"));
    }

    [Fact]
    public async Task Resume_CrashAfterRenameBeforeMetadata_ReappliesMetadataWithoutRecopying()
    {
        Add(@"C:\src\a.bin", 100);
        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        SetTimes(@"C:\src\a.bin", when);
        _rig.Fs.BeforeSetMetadata = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"D:\dst\src\a.bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException();
            }
        };
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\a.bin"));
        Assert.NotEqual(when, _rig.Mem.GetEntry(@"D:\dst\src\a.bin").LastWriteTimeUtc);
        _rig.Fs.BeforeSetMetadata = null;
        _rig.Fs.ClearLog();

        var resumed = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);

        Assert.Equal(1, resumed.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(when, _rig.Mem.GetEntry(@"D:\dst\src\a.bin").LastWriteTimeUtc);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a (2).bin"));
    }
}
