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
        _rig.Workers = 1; // Counts which files finished before the stop, which depends on copying one at a time.
        for (var i = 0; i < 5; i++)
        {
            Add($@"C:\src\f{i}.bin", 10 + i, seed: i);
        }

        _rig.Faults.FailRead(@"C:\src\f2.bin", 0, IoErrorKind.AccessDenied, times: 1); // not a kind the copier waits out
        var id = Guid.NewGuid();
        var first = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);
        Assert.Equal(2, first.Copied);
        _rig.Fs.ClearLog();

        var resumed = await _rig.ResumeAsync(id);

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

        var again = await _rig.ResumeAsync(id);

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
        Assert.Equal(@"0\src\a.bin", part.RelativePath); // journal keys start with the source's index
        Assert.Equal(@"src\a.bin", part.TargetRelativePath);
        Assert.StartsWith(@"src\a.bin.", part.PartRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_TwoSourcesWithTheSameName_SecondIsNotTakenForDone()
    {
        // A copy of two same-named folders is refused by pre-flight (CaseCollision). A move is not checked that way when one source is on
        // the destination's drive (it is a rename), so the journal itself must keep the two apart: keys start with the source's index.
        AddText(@"C:\a\photos\x.jpg", "from C");
        AddText(@"E:\b\photos\x.jpg", "from E");
        _rig.Faults.FailRead(@"E:\b\photos\x.jpg", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        var options = new CopyOptions { IfExists = ConflictRule.Rename, IfUnreadable = FailureRule.Stop };
        var first = await _rig.RunAsync([@"C:\a\photos", @"E:\b\photos"], @"C:\dst", options, CopyMode.Move, jobId: id);
        Assert.Equal(1, first.Copied); // C:\a\photos moved as a whole (journaled done as one folder)
        Assert.True(first.Stopped);

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Copied);
        Assert.Equal("from C", Text(@"C:\dst\photos\x.jpg"));
        Assert.Equal("from E", Text(@"C:\dst\photos\x (2).jpg"));
        Assert.False(_rig.Mem.Exists(@"E:\b\photos\x.jpg"));
    }

    [Fact]
    public async Task Resume_Move_CrashAfterRename_FinishesTheMoveAfterCheckingContent()
    {
        var data = Bytes(300, 4);
        _rig.Mem.AddFile(@"C:\src\a.bin", data);
        _rig.Fs.BeforeSetMetadata = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"D:\dst\src\a.bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException();
            }
        };
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move, jobId: id));
        Assert.True(_rig.Mem.Exists(@"C:\src\a.bin"), "The crash came before the source was removed.");
        _rig.Fs.BeforeSetMetadata = null;
        _rig.Fs.ClearLog();

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Contains(_rig.Fs.Log, l => l == @"OpenRead D:\dst\src\a.bin unbuffered"); // the content was checked first
        Assert.Contains(@"DeleteVerified C:\src\a.bin", _rig.Fs.Log);
        Assert.False(_rig.Mem.Exists(@"C:\src\a.bin"));
        Assert.Equal(data, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }

    [Fact]
    public async Task Resume_Move_DestinationDiffers_SourceIsKept()
    {
        _rig.Mem.AddFile(@"C:\src\a.bin", Bytes(300, 4));
        _rig.Fs.BeforeSetMetadata = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"D:\dst\src\a.bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException();
            }
        };
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move, jobId: id));
        _rig.Fs.BeforeSetMetadata = null;
        _rig.Mem.Delete(@"D:\dst\src\a.bin");
        _rig.Mem.AddFile(@"D:\dst\src\a.bin", Bytes(300, 99)); // same size, other data
        _rig.Fs.ClearLog();

        var resumed = await _rig.ResumeAsync(id);

        Assert.DoesNotContain(@"DeleteVerified C:\src\a.bin", _rig.Fs.Log);
        Assert.Equal(1, resumed.Skipped); // not recognised as ours: the conflict rule (Ask, answered Skip) decides
        Assert.True(_rig.Mem.Exists(@"C:\src\a.bin"));
    }

    [Fact]
    public async Task Resume_TruncatedPartName_RecoversTheRightTarget()
    {
        var name = new string('n', 246) + ".bin"; // 250 characters: the part name has to shorten it
        Add(@"C:\src\" + name, 100);
        var when = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        SetTimes(@"C:\src\" + name, when);
        _rig.Fs.BeforeSetMetadata = path =>
        {
            if (RecordingFileSystem.Plain(path).EndsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException();
            }
        };
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id));
        _rig.Fs.BeforeSetMetadata = null;
        _rig.Fs.ClearLog();

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Copied);
        Assert.Equal(0, resumed.Skipped);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(when, _rig.Mem.GetEntry(@"D:\dst\src\" + name).LastWriteTimeUtc);
    }

    [Fact]
    public async Task Resume_DeletesOnlyTheJournaledPartFiles()
    {
        _rig.Workers = 1; // Cancels when b.bin starts, so how many files finished first depends on copying one at a time.
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

        var resumed = await _rig.ResumeAsync(id);

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

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(when, _rig.Mem.GetEntry(@"D:\dst\src\a.bin").LastWriteTimeUtc);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\a (2).bin"));
    }

    [Fact]
    public async Task Copy_SmallFiles_AreJournaledAsNotOnDiskYet_MoveIsNot()
    {
        Add(@"C:\src\a.bin", 100);
        Add(@"C:\mv\b.bin", 100);
        var copyId = Guid.NewGuid();
        var moveId = Guid.NewGuid();

        await _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: copyId);
        await _rig.RunAsync([@"C:\mv"], @"D:\dst", mode: CopyMode.Move, jobId: moveId);

        using (var copy = JobJournal.Open(_rig.JobsDir, copyId))
        {
            Assert.Equal([@"0\src\a.bin"], copy.NotOnDiskPaths);
        }

        using var move = JobJournal.Open(_rig.JobsDir, moveId);
        Assert.Empty(move.NotOnDiskPaths); // a move forces every file to disk before it deletes the source
        Assert.Single(move.CompletedPaths);
    }

    [Fact]
    public async Task Resume_NotOnDiskFile_StillIntact_IsLeftAloneAndNotCountedAgain()
    {
        Add(@"C:\src\a.bin", 100, seed: 1);
        Add(@"C:\src\b.bin", 100, seed: 2);
        _rig.Faults.FailRead(@"C:\src\b.bin", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        var first = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);
        Assert.Equal(1, first.Copied);
        _rig.Fs.ClearLog();

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Copied); // only b; a was checked, not copied again
        Assert.Contains(_rig.Fs.Log, l => l == @"OpenRead D:\dst\src\a.bin unbuffered"); // a was read back and compared
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith(@"CreateWrite D:\dst\src\a.bin", StringComparison.Ordinal));
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\a.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }

    [Fact]
    public async Task Resume_NotOnDiskFile_LeftBlankByPowerCut_IsReplacedWithoutAConflictQuestion()
    {
        Add(@"C:\src\a.bin", 100, seed: 1);
        Add(@"C:\src\b.bin", 100, seed: 2);
        _rig.Faults.FailRead(@"C:\src\b.bin", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        var options = new CopyOptions { IfUnreadable = FailureRule.Stop, IfExists = ConflictRule.Skip };
        await _rig.RunAsync([@"C:\src"], @"D:\dst", options, jobId: id);
        _rig.Mem.AddFile(@"D:\dst\src\a.bin", new byte[100]); // the name reached the disk, the data did not

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(2, resumed.Copied);
        Assert.Equal(0, resumed.Skipped); // Skip would have kept the blank file: this job's own copy is replaced instead
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\a.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
    }

    [Fact]
    public async Task Resume_PowerCutLostTheJournalTail_BlankFileIsStillReplaced()
    {
        Add(@"C:\src\a.bin", 100, seed: 1);
        Add(@"C:\src\b.bin", 100, seed: 2);
        _rig.Faults.FailRead(@"C:\src\b.bin", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        var options = new CopyOptions { IfUnreadable = FailureRule.Stop, IfExists = ConflictRule.Skip };
        await _rig.RunAsync([@"C:\src"], @"D:\dst", options, jobId: id);

        // The power cut kept the rename and the dates but lost the data, and the journal's last records (a's part and done lines) with it.
        var journalPath = Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal");
        var lines = File.ReadAllLines(journalPath).Where(l => !l.Contains(@"a.bin", StringComparison.Ordinal)).ToArray();
        File.WriteAllText(journalPath, string.Join('\n', lines) + "\n");
        var kept = _rig.Mem.GetEntry(@"D:\dst\src\a.bin");
        _rig.Mem.AddFile(@"D:\dst\src\a.bin", new byte[100]);
        _rig.Mem.SetMetadata(@"D:\dst\src\a.bin", FileAttributes.Normal, kept.CreationTimeUtc, kept.LastWriteTimeUtc);

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(0, resumed.Skipped); // Skip would have kept the blank file
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\a.bin"), _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }

    [Fact]
    public async Task Resume_FileChangedAfterTheJobStopped_IsNotTreatedAsOurs()
    {
        Add(@"C:\src\a.bin", 100, seed: 1);
        Add(@"C:\src\b.bin", 100, seed: 2);
        _rig.Faults.FailRead(@"C:\src\b.bin", 0, IoErrorKind.AccessDenied, times: 1);
        var id = Guid.NewGuid();
        var options = new CopyOptions { IfUnreadable = FailureRule.Stop, IfExists = ConflictRule.Skip };
        await _rig.RunAsync([@"C:\src"], @"D:\dst", options, jobId: id);
        var edited = Bytes(100, seed: 77);
        _rig.Mem.AddFile(@"D:\dst\src\a.bin", edited); // the user edits the copy later, before resuming
        _rig.Mem.SetMetadata(@"D:\dst\src\a.bin", FileAttributes.Normal, DateTime.UtcNow, DateTime.UtcNow.AddHours(2));

        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(1, resumed.Skipped); // the user's rule (Skip) decides, as for any file that is not this job's own
        Assert.Equal(edited, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.bin"));
    }
}
