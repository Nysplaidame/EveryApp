using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

public sealed class TreeCopierTests : IDisposable
{
    private readonly TreeCopierRig _rig = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static bool IsPartOf(string path, string name) =>
        RecordingFileSystem.Plain(path).Contains(name + ".", StringComparison.OrdinalIgnoreCase) && path.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase);

    private void Add(string path, int size, int seed = 1) => _rig.Mem.AddFile(path, Bytes(size, seed));

    private void AddText(string path, string text) => _rig.Mem.AddFile(path, Encoding.UTF8.GetBytes(text));

    private string Text(string path) => Encoding.UTF8.GetString(_rig.Mem.ReadAllBytes(path));

    private void SetTimes(string path, DateTime lastWriteUtc)
    {
        var e = _rig.Mem.GetEntry(path);
        _rig.Mem.SetMetadata(path, e.Attributes, lastWriteUtc, lastWriteUtc);
    }

    // ---------------------------------------------------------------- basic copy

    [Fact]
    public async Task Run_CopiesTree_AllVerified()
    {
        var a = Bytes(10, 1);
        var b = Bytes(5000, 2);
        var c = Bytes(70_000, 3);
        _rig.Mem.AddFile(@"C:\src\a.txt", a);
        _rig.Mem.AddFile(@"C:\src\sub\b.bin", b);
        _rig.Mem.AddFile(@"C:\src\sub\deep\c.bin", c);
        _rig.Mem.CreateDirectory(@"C:\src\empty");
        var id = Guid.NewGuid();

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);

        Assert.Equal(3, result.Copied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Empty(result.Problems);
        Assert.Empty(result.PreflightIssues);
        Assert.True(result.AllVerified);
        Assert.Equal(a, _rig.Mem.ReadAllBytes(@"D:\dst\src\a.txt"));
        Assert.Equal(b, _rig.Mem.ReadAllBytes(@"D:\dst\src\sub\b.bin"));
        Assert.Equal(c, _rig.Mem.ReadAllBytes(@"D:\dst\src\sub\deep\c.bin"));
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\empty"), "Empty folders are created too.");
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
        Assert.Equal(3, journal.CompletedPaths.Count);
        Assert.Contains(@"src\sub\deep\c.bin", journal.CompletedPaths);
    }

    [Fact]
    public async Task Run_SingleFileSource_CopiesIntoDestination()
    {
        Add(@"C:\docs\report.txt", 100);

        var result = await _rig.RunAsync([@"C:\docs\report.txt"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\docs\report.txt"), _rig.Mem.ReadAllBytes(@"D:\dst\report.txt"));
    }

    [Fact]
    public async Task Run_VerifyOff_NothingReadBack_AndNotAllVerified()
    {
        Add(@"C:\src\a.bin", 1000);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { Verify = false });

        Assert.Equal(1, result.Copied);
        Assert.False(result.AllVerified);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.EndsWith(" unbuffered", StringComparison.Ordinal) && l.Contains(@"D:\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_CreatesFoldersBeforeTheirFiles_SmallFilesFirst()
    {
        Add(@"C:\src\big.bin", 3000);
        Add(@"C:\src\small.bin", 10);
        Add(@"C:\src\mid.bin", 500);
        Add(@"C:\src\sub\inner.bin", 20);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(4, result.Copied);
        var log = _rig.Fs.Log;
        int IndexOf(Func<string, bool> match) => log.FindIndex(l => match(l));
        int Commit(string name) => IndexOf(l => l.StartsWith("Move ", StringComparison.Ordinal) && l.EndsWith(@"-> D:\dst\src\" + name, StringComparison.OrdinalIgnoreCase));
        var createSrc = IndexOf(l => l == @"CreateDirectory D:\dst\src");
        var createSub = IndexOf(l => l == @"CreateDirectory D:\dst\src\sub");
        Assert.True(createSrc >= 0 && createSub >= 0);
        Assert.True(createSrc < Commit("small.bin"));
        Assert.True(Commit("small.bin") < Commit("mid.bin"));
        Assert.True(Commit("mid.bin") < Commit("big.bin"));
        Assert.True(createSub < Commit(@"sub\inner.bin"));
    }

    [Fact]
    public async Task Copy_FolderLink_SkippedAndReported()
    {
        Add(@"C:\other\x.bin", 10);
        Add(@"C:\src\a.bin", 10);
        _rig.Mem.AddJunction(@"C:\src\link", @"C:\other");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\link"));
        var problem = Assert.Single(result.Problems);
        Assert.Equal(CopyMessages.LinkSkipped, problem.Message);
        Assert.Equal(IoErrorKind.None, problem.Kind);
    }

    [Fact]
    public async Task Copy_FileSymlink_CopiedAsTargetContents()
    {
        _rig.Mem.AddFile(@"C:\src\shortcut.txt", Encoding.UTF8.GetBytes("target data"), reparseTag: 0xA000000C);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Copied);
        Assert.Equal("target data", Text(@"D:\dst\src\shortcut.txt"));
        Assert.False(_rig.Mem.GetEntry(@"D:\dst\src\shortcut.txt").IsReparsePoint, "The copy is an ordinary file, not a link.");
    }

    // ---------------------------------------------------------------- conflicts

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

    // ---------------------------------------------------------------- failures

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

    // ---------------------------------------------------------------- verify

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
    public async Task Verify_ReadsDestinationUnbuffered()
    {
        Add(@"C:\src\a.bin", 3000);

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Contains(_rig.Fs.Log, l => l.StartsWith(@"OpenRead D:\dst\src\a.bin.", StringComparison.Ordinal) && l.EndsWith(".ferrypart unbuffered", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- dry run, preflight, overlap

    [Fact]
    public async Task DryRun_WritesNothing_ReportsPlan()
    {
        AddText(@"C:\src\a.txt", "a");
        AddText(@"C:\src\sub\b.txt", "b");
        AddText(@"D:\dst\src\a.txt", "existing");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { DryRun = true, IfExists = ConflictRule.Rename });

        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(0, _rig.Fs.Count("CreateDirectory"));
        Assert.Equal(0, _rig.Fs.Count("Move"));
        Assert.Equal(0, _rig.Fs.Count("Delete"));
        Assert.Equal(0, _rig.Fs.Count("SetMetadata"));
        Assert.False(_rig.AnyJournal());
        Assert.Equal(0, _rig.Sleep.Acquired);
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\sub"));
        Assert.Contains(result.Plan, p => p.Action == PlannedAction.CreateFolder && p.Destination.EndsWith(@"D:\dst\src\sub", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Plan, p => p.Action == PlannedAction.CopyWithNewName && p.Destination.EndsWith(@"D:\dst\src\a (2).txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Plan, p => p.Action == PlannedAction.Copy && p.Destination.EndsWith(@"D:\dst\src\sub\b.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.Copied);
    }

    [Fact]
    public async Task Preflight_IssuesReturnedBeforeAnyWrite()
    {
        Add(@"C:\src\a.bin", 5000);
        _rig.Mem.FreeSpaceBytes = 100;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.True(result.Stopped);
        Assert.Contains(result.PreflightIssues, i => i.Code == PreflightCode.NotEnoughSpace);
        Assert.Equal(0, result.Copied);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(0, _rig.Fs.Count("CreateDirectory"));
        Assert.Equal(0, _rig.Fs.Count("Move"));
        Assert.False(_rig.AnyJournal());
    }

    [Fact]
    public async Task Preflight_UsesDestinationFileSystem()
    {
        Add(@"C:\src\a*b.txt", 10);
        _rig.Destination.FileSystem = "SMB";

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Contains(result.PreflightIssues, i => i.Code == PreflightCode.IllegalCharForDestination);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task Preflight_LongPathWarning_DoesNotStopTheCopy()
    {
        Add(@"C:\src\" + new string('n', 250) + ".txt", 10);
        _rig.Destination.LongPathsEnabled = false;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Contains(result.PreflightIssues, i => i.Code == PreflightCode.PathTooLong);
        Assert.False(result.Stopped);
        Assert.Equal(1, result.Copied);
    }

    [Fact]
    public async Task DestinationInsideSource_Refused_NothingWritten()
    {
        Add(@"C:\a\f.bin", 10);

        var result = await _rig.RunAsync([@"C:\a"], @"C:\a\b");

        var issue = Assert.Single(result.PreflightIssues);
        Assert.Equal(PreflightCode.DestinationInsideSource, issue.Code);
        Assert.Equal(CopyMessages.DestinationInsideSource, issue.Message);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(0, _rig.Fs.Count("CreateDirectory"));
        Assert.False(_rig.AnyJournal());
    }

    [Fact]
    public async Task DestinationInsideSource_ThroughALink_DetectedByIdentity()
    {
        Add(@"C:\a\f.bin", 10);
        _rig.Mem.AddJunction(@"D:\j", @"C:\a"); // D:\j leads into the source, though the text looks unrelated

        var result = await _rig.RunAsync([@"C:\a"], @"D:\j\b");

        Assert.Equal(PreflightCode.DestinationInsideSource, Assert.Single(result.PreflightIssues).Code);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.Equal(0, _rig.Fs.Count("CreateDirectory"));
    }

    [Fact]
    public async Task CopyIntoItsOwnFolder_Refused()
    {
        Add(@"C:\p\a.bin", 10);

        var result = await _rig.RunAsync([@"C:\p\a.bin"], @"C:\p", mode: CopyMode.Move);

        var issue = Assert.Single(result.PreflightIssues);
        Assert.Equal(CopyMessages.SameAsSource, issue.Message);
        Assert.True(_rig.Mem.Exists(@"C:\p\a.bin"));
        Assert.Equal(0, _rig.Fs.Count("Move"));
        Assert.Equal(0, _rig.Fs.Count("Delete"));
    }

    // ---------------------------------------------------------------- moves

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

    // ---------------------------------------------------------------- progress, sleep, scheduler

    [Fact]
    public async Task Progress_Phases_InOrder()
    {
        Add(@"C:\src\a.bin", 3000);
        Add(@"C:\src\b.bin", 5000);
        var progress = new ListProgress();

        await _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress);

        var phases = new List<string>();
        foreach (var r in progress.Reports)
        {
            if (phases.Count == 0 || phases[^1] != r.Phase)
            {
                phases.Add(r.Phase);
            }
        }

        Assert.Equal("Checking", phases[0]);
        var firstCopy = phases.IndexOf("Copying");
        Assert.True(firstCopy > 0);
        Assert.True(phases.IndexOf("Verifying") > firstCopy);
        Assert.DoesNotContain("Checking", phases.Skip(firstCopy));
        Assert.All(phases, p => Assert.True(p is "Checking" or "Waiting" or "Copying" or "Verifying", p));
        var last = progress.Reports[^1];
        Assert.Equal(8000, last.BytesTotal);
        Assert.Equal(8000, last.BytesDone);
        Assert.Equal(2, last.FilesDone);
        Assert.Equal(2, last.FilesTotal);
    }

    [Fact]
    public async Task Progress_ShowsWaiting_WhileTheDriveIsBusy()
    {
        Add(@"C:\src\a.bin", 100);
        _rig.Devices.Map(@"C:\src", "disk-c").Map(@"D:\dst", "disk-d").Map(@"D:\other", "disk-d");
        var other = await _rig.Scheduler.AcquireAsync(@"D:\other", @"D:\other", Ct);
        var progress = new ListProgress();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progress.OnReport = p =>
        {
            if (p.Phase == "Waiting")
            {
                waiting.TrySetResult();
            }
        };

        var run = Task.Run(() => _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress), Ct);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        await other.DisposeAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(1, result.Copied);
    }

    [Fact]
    public async Task Run_PreventsSleepWhileActive()
    {
        Add(@"C:\src\a.bin", 100);
        var activeDuringCopy = -1;
        _rig.Fs.AfterCreateWrite = _ => activeDuringCopy = _rig.Sleep.Active;

        await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, activeDuringCopy);
        Assert.Equal(1, _rig.Sleep.Acquired);
        Assert.Equal(1, _rig.Sleep.Released);
        Assert.Equal(0, _rig.Sleep.Active);
    }

    [Fact]
    public async Task Cancel_ReleasesEverything_AndLeavesJobResumable()
    {
        Add(@"C:\src\a.bin", 100);
        Add(@"C:\src\b.bin", 200);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _rig.Fs.AfterCreateWrite = path =>
        {
            if (IsPartOf(path, "b.bin"))
            {
                cts.Cancel();
            }
        };
        var id = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id, ct: cts.Token));

        Assert.Equal(0, _rig.Sleep.Active);
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        await using (await _rig.Scheduler.AcquireAsync(@"C:\src", @"D:\dst", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct))
        {
            // the scheduler slot was given back
        }

        _rig.Fs.AfterCreateWrite = null;
        var resumed = await _rig.NewCopier().ResumeAsync(id, new PauseGate(), new ListProgress(), Ct);
        Assert.Equal(1, resumed.Copied);
        Assert.True(_rig.Mem.Exists(@"D:\dst\src\b.bin"));
    }

    // ---------------------------------------------------------------- resume and part files

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

    // ---------------------------------------------------------------- checksums, permissions, streams, cloning

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

    // ---------------------------------------------------------------- ETA

    [Fact]
    public void SpeedWindow_AveragesTheLastTenSeconds()
    {
        var window = new SpeedWindow(TimeSpan.TicksPerSecond);
        long bytes = 0;
        for (var s = 0; s <= 20; s++)
        {
            window.Add(s * TimeSpan.TicksPerSecond, bytes);
            bytes += 1_000_000; // 1 MB/s for 20 s
        }

        for (var s = 21; s <= 30; s++)
        {
            bytes += 3_000_000; // then 3 MB/s
            window.Add(s * TimeSpan.TicksPerSecond, bytes);
        }

        Assert.InRange(window.BytesPerSecond, 2_900_000, 3_100_000);
    }

    [Fact]
    public void SpeedWindow_NoSpeedYet_IsZero()
    {
        var window = new SpeedWindow(TimeSpan.TicksPerSecond);
        window.Add(0, 0);
        Assert.Equal(0, window.BytesPerSecond);
    }
}

/// <summary>Regression test for the shared in-memory fake.</summary>
public sealed class InMemoryFakeTests
{
    [Fact]
    public void Move_Folder_MovesTheWholeTree()
    {
        var m = new EveryApp.Core.Tests.Fakes.InMemoryFileSystem();
        m.AddFile(@"C:\src\a.bin", [1]);
        m.AddFile(@"C:\src\sub\b.bin", [2]);
        m.CreateDirectory(@"C:\dst");

        m.Move(@"C:\src", @"C:\dst\src", overwrite: false);

        Assert.Equal([1], m.ReadAllBytes(@"C:\dst\src\a.bin"));
        Assert.Equal([2], m.ReadAllBytes(@"C:\dst\src\sub\b.bin"));
        Assert.False(m.Exists(@"C:\src"));
    }
}
