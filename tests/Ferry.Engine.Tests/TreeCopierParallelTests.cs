using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Scheduling;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: several files copied at once on solid-state drives, every decision still made one file at a time in planned order.</summary>
public sealed class TreeCopierParallelTests : TreeCopierTestBase
{
    private const int MiB = 1024 * 1024;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public TreeCopierParallelTests()
    {
        _rig.Workers = 4;
        _rig.DriveKinds.Kind = DriveKind.SolidState;
    }

    [Fact]
    public async Task SolidState_CopiesSeveralAtOnce()
    {
        AddFiles(6);
        var hold = new WriteHold(_ => true);
        _rig.Fs.BeforeWrite = hold.WaitAsync;

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst");
        await hold.Entered(4).WaitAsync(Patience, Ct);

        Assert.Equal(4, PartsCreated());
        Assert.Equal(4, hold.Count);
        hold.Release();
        var result = await job;

        Assert.Equal(6, result.Copied);
        AssertSameAsSource(6);
    }

    /// <summary>A volume has one sector size: a checked job asks once per volume, not twice for every file.</summary>
    [Fact]
    public async Task TreeJob_CheckedCopy_AsksSectorSizeOncePerVolume()
    {
        const int files = 1000;
        for (var i = 0; i < files; i++)
        {
            _rig.Mem.AddFile($@"C:\src\f{i}.bin", Bytes(100 + (i % 7), seed: i));
        }

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { Verify = true });

        Assert.Equal(files, result.Copied);
        Assert.True(_rig.Fs.SectorSizeCalls <= 2, $"asked {_rig.Fs.SectorSizeCalls} times");
    }

    /// <summary>Without a job's shared cache (a single file) the copier still asks the file system itself.</summary>
    [Fact]
    public async Task FileCopier_WithoutHooks_StillAsksSectorSize()
    {
        _rig.Mem.AddFile(@"C:\one.bin", Bytes(100, seed: 1));
        using var pool = new BufferPool();

        var outcome = await new FileCopier(_rig.Fs, pool).CopyAsync(@"C:\one.bin", @"D:\one.bin", new CopyOptions { RescueMode = true }, new PauseGate(),
            new LatestProgress(), null, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.True(_rig.Fs.SectorSizeCalls >= 2);
    }

    [Fact]
    public async Task Spinning_OneAtATime()
    {
        _rig.DriveKinds.Kind = DriveKind.Spinning;
        await AssertOneAtATime(new CopyOptions());
    }

    [Fact]
    public async Task Unknown_OneAtATime()
    {
        _rig.DriveKinds.Kind = DriveKind.Unknown;
        await AssertOneAtATime(new CopyOptions());
    }

    [Fact]
    public async Task NotFixedLocal_OneAtATime()
    {
        _rig.Destination.FixedLocal = false;
        await AssertOneAtATime(new CopyOptions());
    }

    [Fact]
    public async Task Rescue_OneAtATime() => await AssertOneAtATime(new CopyOptions { RescueMode = true });

    public static TheoryData<string> Cases() => new() { "copy", "verify", "move", "checksums", "rename" };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SameResults_Workers1And4(string kind)
    {
        using var single = new TreeCopierRig { Workers = 1 };
        var options = kind switch
        {
            "verify" => new CopyOptions { Verify = true },
            "checksums" => new CopyOptions { ExportChecksums = true },
            "rename" => new CopyOptions { IfExists = ConflictRule.Rename },
            _ => new CopyOptions(),
        };
        var mode = kind == "move" ? CopyMode.Move : CopyMode.Copy;
        foreach (var rig in new[] { _rig, single })
        {
            Populate(rig, existingTargets: kind == "rename");
        }

        var parallel = await _rig.RunAsync([@"C:\src"], @"D:\dst", options, mode);
        var sequential = await single.RunAsync([@"C:\src"], @"D:\dst", options, mode);

        Assert.Equal(Tree(single, @"D:\dst"), Tree(_rig, @"D:\dst"));
        Assert.Equal(Tree(single, @"C:\src"), Tree(_rig, @"C:\src"));
        Assert.Equal(
            (sequential.Copied, sequential.Skipped, sequential.Failed, sequential.AllVerified),
            (parallel.Copied, parallel.Skipped, parallel.Failed, parallel.AllVerified));
        Assert.True(parallel.Copied > 0);
    }

    [Fact]
    public async Task RenameCollision_MatchesSequential()
    {
        using var single = new TreeCopierRig { Workers = 1 };
        var options = new CopyOptions { IfExists = ConflictRule.Rename };
        foreach (var rig in new[] { _rig, single })
        {
            rig.Mem.AddFile(@"C:\src\a.txt", Bytes(10, seed: 1));
            rig.Mem.AddFile(@"C:\src\a (2).txt", Bytes(20, seed: 2));
            rig.Mem.AddFile(@"D:\dst\src\a.txt", Bytes(5, seed: 3));
        }

        var hold = new WriteHold(path => IsPartOf(path, "a (2).txt")); // a.txt's copy, renamed to a (2).txt
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        var id = Guid.NewGuid();
        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", options, jobId: id);
        await hold.Entered(1).WaitAsync(Patience, Ct);
        hold.Release();
        var parallel = await job;
        var sequential = await single.RunAsync([@"C:\src"], @"D:\dst", options);

        Assert.Equal(Tree(single, @"D:\dst"), Tree(_rig, @"D:\dst"));
        Assert.Equal((sequential.Copied, sequential.Skipped, sequential.Failed), (parallel.Copied, parallel.Skipped, parallel.Failed));
        Assert.Equal(2, parallel.Copied);
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        var targets = journal.PartFiles.Select(p => p.TargetRelativePath).ToList();
        Assert.Equal(targets.Count, targets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task StopRule_NoNewFilesAfterFailure_InFlightFinish()
    {
        AddFiles(8);
        var hold = new WriteHold(path => IsPartOf(path, "f1.bin") || IsPartOf(path, "f3.bin") || IsPartOf(path, "f4.bin"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        _rig.Fs.OnOpenRead = (path, _) =>
        {
            if (!RecordingFileSystem.Plain(path).Equals(@"C:\src\f2.bin", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // File 2 fails only once files 1, 3 and 4 are already copying.
            hold.Entered(3).Wait(Patience, Ct);
            return new FileSystemException(path, IoErrorKind.AccessDenied, null);
        };

        // File 2 is the first to end (the others are held), and the job is stopped before its end is reported: let the others go then.
        var progress = new ListProgress
        {
            OnReport = p =>
            {
                if (p.FilesDone >= 1)
                {
                    hold.Release();
                }
            },
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, progress: progress);

        Assert.True(result.Stopped);
        Assert.Equal(3, result.Copied);
        Assert.Equal(1, result.Failed);
        foreach (var i in new[] { 1, 3, 4 })
        {
            Assert.Equal(_rig.Mem.ReadAllBytes($@"C:\src\f{i}.bin"), _rig.Mem.ReadAllBytes($@"D:\dst\src\f{i}.bin"));
        }

        for (var i = 5; i <= 8; i++)
        {
            Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith($@"OpenRead C:\src\f{i}.bin", StringComparison.Ordinal));
        }

        Assert.Empty(_rig.PartFiles(@"D:\dst"));
    }

    [Fact]
    public async Task Cancel_WithSeveralInFlight_NoPartsLeftAndResumable()
    {
        AddFiles(6);
        var hold = new WriteHold(_ => true);
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var id = Guid.NewGuid();

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id, ct: cts.Token);
        await hold.Entered(4).WaitAsync(Patience, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        for (var i = 1; i <= 6; i++)
        {
            var target = $@"D:\dst\src\f{i}.bin";
            Assert.True(!_rig.Mem.Exists(target) || _rig.Mem.ReadAllBytes(target).AsSpan().SequenceEqual(_rig.Mem.ReadAllBytes($@"C:\src\f{i}.bin")));
        }

        _rig.Fs.BeforeWrite = null;
        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(0, resumed.Failed);
        AssertSameAsSource(6);
    }

    [Fact]
    public async Task UnexpectedWorkerException_AwaitsOthersThenThrows()
    {
        AddFiles(6);
        var hold = new WriteHold(path => IsPartOf(path, "f1.bin") || IsPartOf(path, "f2.bin") || IsPartOf(path, "f3.bin"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        var thrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Fs.OnWrite = (path, _) =>
        {
            if (!IsPartOf(path, "f4.bin"))
            {
                return null;
            }

            thrown.TrySetResult();
            return new InvalidOperationException("Unexpected.");
        };
        var id = Guid.NewGuid();

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);
        await hold.Entered(3).WaitAsync(Patience, Ct);
        await thrown.Task.WaitAsync(Patience, Ct);

        Assert.False(job.IsCompleted, "the job must wait for the copies still running");
        hold.Release();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => job);
        Assert.Equal("Unexpected.", ex.Message);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(_rig.Mem.ReadAllBytes($@"C:\src\f{i}.bin"), _rig.Mem.ReadAllBytes($@"D:\dst\src\f{i}.bin"));
        }

        using var closed = new FileStream(Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ConflictAsk_OneQuestionAtATime_NoNewStartWhileOpen()
    {
        AddFiles(4);
        Add(@"D:\dst\src\f2.bin", 5, seed: 50);
        Add(@"D:\dst\src\f4.bin", 5, seed: 51);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Resolver.Answer = ConflictRule.Overwrite;
        _rig.Resolver.Hold = async (source, ct) =>
        {
            if (source.FullPath.EndsWith("f2.bin", StringComparison.OrdinalIgnoreCase))
            {
                open.TrySetResult();
                await answer.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        };

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask });
        await open.Task.WaitAsync(Patience, Ct);

        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("CreateWrite", StringComparison.Ordinal) && (IsPartOf(l, "f3.bin") || IsPartOf(l, "f4.bin")));
        Assert.Single(_rig.Resolver.Questions);
        answer.SetResult();
        var result = await job;

        Assert.Equal(4, result.Copied);
        Assert.Equal(["f2.bin", "f4.bin"], _rig.Resolver.Questions.Select(q => Path.GetFileName(q.Source.FullPath)));
        AssertSameAsSource(4);
    }

    [Fact]
    public async Task StopWhileQuestionOpen_AnsweredFileIsNotCopied()
    {
        AddFiles(3);
        Add(@"D:\dst\src\f2.bin", 5, seed: 50);
        var existing = _rig.Mem.ReadAllBytes(@"D:\dst\src\f2.bin");
        _rig.Fs.OnOpenRead = (path, _) => RecordingFileSystem.Plain(path).Equals(@"C:\src\f1.bin", StringComparison.OrdinalIgnoreCase)
            ? new FileSystemException(path, IoErrorKind.AccessDenied, null)
            : null;

        // f2's question stays open until f1's copy has failed and stopped the job (f1's end is reported after the stop).
        var f1Ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ListProgress
        {
            OnReport = p =>
            {
                if (p.FilesDone >= 1)
                {
                    f1Ended.TrySetResult();
                }
            },
        };
        _rig.Resolver.Answer = ConflictRule.Overwrite;
        _rig.Resolver.Hold = (_, ct) => f1Ended.Task.WaitAsync(ct);

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask, IfUnreadable = FailureRule.Stop }, progress: progress);

        Assert.True(result.Stopped);
        Assert.Equal((0, 0, 1), (result.Copied, result.Skipped, result.Failed));
        Assert.Single(_rig.Resolver.Questions);
        Assert.Equal(existing, _rig.Mem.ReadAllBytes(@"D:\dst\src\f2.bin")); // the answer came after the stop: never acted on
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith(@"OpenRead C:\src\f2.bin", StringComparison.Ordinal) || l.StartsWith(@"OpenRead C:\src\f3.bin", StringComparison.Ordinal));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        var last = progress.Reports[^1];
        Assert.Equal(0, last.InFlight); // f2's progress was ended
    }

    [Fact]
    public async Task Resume_AfterKillWithSeveralInFlight_CompletesWithSameFiles()
    {
        AddFiles(6);
        var hold = new WriteHold(path => IsPartOf(path, "f1.bin") || IsPartOf(path, "f2.bin") || IsPartOf(path, "f4.bin"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        _rig.Fs.BeforeSetMetadata = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"D:\dst\src\f3.bin", StringComparison.OrdinalIgnoreCase))
            {
                hold.Entered(3).Wait(Patience, Ct); // f1, f2 and f4 are copying when the job "dies" here
                hold.Release(); // they finish while the job dies
                throw new SimulatedCrashException();
            }
        };
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<SimulatedCrashException>(() => _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id));
        _rig.Fs.BeforeWrite = null;
        _rig.Fs.BeforeSetMetadata = null;
        _rig.Fs.ClearLog();
        var resumed = await _rig.ResumeAsync(id);

        Assert.Equal(0, resumed.Failed);
        Assert.Equal(0, resumed.Skipped);
        Assert.True(resumed.Copied >= 2, $"copied {resumed.Copied}"); // f3 recovered, f6 at least (f5 may have started before the job died)
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("CreateWrite", StringComparison.Ordinal) && !IsPartOf(l, "f5.bin") && !IsPartOf(l, "f6.bin"));
        Assert.Empty(_rig.PartFiles(@"D:\dst"));
        Assert.False(_rig.Mem.Exists(@"D:\dst\src\f3 (2).bin"));
        AssertSameAsSource(6);
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
    }

    [Fact]
    public async Task JournalAndFolders_FinishAfterAllCopies()
    {
        AddFiles(5);
        var hold = new WriteHold(path => IsPartOf(path, "f5.bin"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        var id = Guid.NewGuid();

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", jobId: id);
        await hold.Entered(1).WaitAsync(Patience, Ct);

        Assert.DoesNotContain(_rig.Fs.Log, l => l.Equals(@"SetMetadata D:\dst\src", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(", skipped ", JournalText(id), StringComparison.Ordinal); // the finished record ("copied n, skipped n, ...") is not there
        Assert.False(job.IsCompleted);
        hold.Release();
        await job;

        Assert.Contains(_rig.Fs.Log, l => l.Equals(@"SetMetadata D:\dst\src", StringComparison.OrdinalIgnoreCase));
        using var journal = JobJournal.Open(_rig.JobsDir, id);
        Assert.True(journal.IsFinished);
    }

    [Fact]
    public async Task SpeedLimit_HoldsAcrossSeveralWorkers()
    {
        const int Files = 8;
        const int Size = 128 * 1024;
        const long Rate = 256 * 1024; // 1 MiB in all: at least 4 seconds
        for (var i = 1; i <= Files; i++)
        {
            Add($@"C:\src\f{i}.bin", Size, seed: i);
        }

        var time = new FrozenTime();
        _rig.Time = time;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { SpeedLimitBytesPerSecond = Rate });

        // The clock never moves, so with one limit for the whole job each chunk is due after every byte before it, whichever copy sent it:
        // the last wait is the time all the bytes need. A limit per copy would make no wait longer than one file's time.
        Assert.Equal(Files, result.Copied);
        Assert.True(_rig.Workers > 1);
        var floor = TimeSpan.FromSeconds((double)Files * Size / Rate) - TimeSpan.FromMilliseconds(50);
        Assert.True(time.LongestDelay >= floor, $"the last chunk was due after {time.LongestDelay}; {Files * Size} bytes at {Rate} bytes a second need {floor}.");
    }

    [Fact]
    public Task ShortNameLookalike_WaitsForSameFolderCopies() => AssertWaitsForHeldCopy(@"C:\src\verylongname.txt", @"VERYLO~1.TXT");

    [Fact]
    public Task TrailingDotName_WaitsForSameFolderCopies() => AssertWaitsForHeldCopy(@"C:\src\verylongname.txt", "later.");

    [Fact]
    public Task TrailingSpaceName_WaitsForSameFolderCopies() => AssertWaitsForHeldCopy(@"C:\src\verylongname.txt", "later ");

    /// <summary>
    /// A held copy of <paramref name="first"/>; the next file <paramref name="lookalike"/> (same folder, an existing target, so a question is
    /// asked once it is decided) must not be decided until the held copy has ended.
    /// </summary>
    private async Task AssertWaitsForHeldCopy(string first, string lookalike)
    {
        Add(first, 101, seed: 1);
        Add(@"C:\src\" + lookalike, 102, seed: 2);
        Add(@"D:\dst\src\" + lookalike, 5, seed: 3);
        var hold = new WriteHold(path => IsPartOf(path, "verylongname.txt"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Resolver.Hold = (_, _) =>
        {
            asked.TrySetResult();
            return Task.CompletedTask;
        };

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask });
        await hold.Entered(1).WaitAsync(Patience, Ct);

        // Nothing can be waited for here (the file must NOT be decided), so give the dispatcher a short while to wrongly reach it.
        var wrongly = await Task.WhenAny(asked.Task, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        Assert.NotSame(asked.Task, wrongly);
        Assert.Empty(_rig.Resolver.Questions);

        hold.Release();
        var result = await job;

        Assert.Single(_rig.Resolver.Questions);
        Assert.Equal(1, result.Copied);
        Assert.Equal(_rig.Mem.ReadAllBytes(first), _rig.Mem.ReadAllBytes(@"D:\dst\src\verylongname.txt"));
    }

    [Fact]
    public async Task ShortNameLookalikeFolder_WaitsForSameFolderCopies()
    {
        Add(@"C:\src\verylongname.txt", 101, seed: 1);
        Add(@"C:\src\VERYLO~1\inner.txt", 5, seed: 2);
        var hold = new WriteHold(path => IsPartOf(path, "verylongname.txt"));
        _rig.Fs.BeforeWrite = hold.WaitAsync;

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst");
        await hold.Entered(1).WaitAsync(Patience, Ct);

        // The folder name may be the same name as the file being committed next to it: it must NOT be decided yet (bounded wait, can only false-pass).
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("CreateDirectory ", StringComparison.Ordinal) && l.Contains("VERYLO~1", StringComparison.OrdinalIgnoreCase));

        hold.Release();
        var result = await job;

        Assert.Equal(2, result.Copied);
        Assert.Equal(_rig.Mem.ReadAllBytes(@"C:\src\VERYLO~1\inner.txt"), _rig.Mem.ReadAllBytes(@"D:\dst\src\VERYLO~1\inner.txt"));
    }

    [Fact]
    public async Task StopWhileQuestionOpen_SkipAnswer_IsNotCountedAsSkipped()
    {
        AddFiles(3);
        Add(@"D:\dst\src\f2.bin", 5, seed: 50);
        var id = Guid.NewGuid();
        _rig.Fs.OnOpenRead = (path, _) => RecordingFileSystem.Plain(path).Equals(@"C:\src\f1.bin", StringComparison.OrdinalIgnoreCase)
            ? new FileSystemException(path, IoErrorKind.AccessDenied, null)
            : null;
        var f1Ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ListProgress
        {
            OnReport = p =>
            {
                if (p.FilesDone >= 1)
                {
                    f1Ended.TrySetResult();
                }
            },
        };
        _rig.Resolver.Answer = ConflictRule.Skip; // the answer comes after the stop: nothing may be recorded for it
        _rig.Resolver.Hold = (_, ct) => f1Ended.Task.WaitAsync(ct);

        var result = await _rig.RunAsync(
            [@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask, IfUnreadable = FailureRule.Stop }, progress: progress, jobId: id);

        Assert.True(result.Stopped);
        Assert.Equal((0, 0, 1), (result.Copied, result.Skipped, result.Failed)); // as one at a time: f2 is never reached
        Assert.DoesNotContain("f2.bin", JournalText(id), StringComparison.Ordinal); // no skipped record for it
        var last = progress.Reports[^1];
        Assert.Equal(0, last.InFlight);
        Assert.Equal(1, last.FilesDone); // f1 only: f2 was never copied, so it is not counted as done
    }

    [Fact]
    public async Task StopWhileWaitingForLookalike_NothingElseIsDecided()
    {
        Add(@"C:\src\verylongname.txt", 101, seed: 1);
        Add(@"C:\src\VERYLO~1.TXT", 102, seed: 2);
        Add(@"D:\dst\src\VERYLO~1.TXT", 5, seed: 3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Fs.BeforeWrite = async (path, _, ct) =>
        {
            if (IsPartOf(path, "verylongname.txt"))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
                throw new FileSystemException(path, IoErrorKind.AccessDenied, null); // this copy fails once the other file is already waiting for it
            }
        };
        _rig.Resolver.Answer = ConflictRule.Skip;

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfExists = ConflictRule.Ask, IfUnreadable = FailureRule.Stop });
        await entered.Task.WaitAsync(Patience, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct); // let the look-alike file reach its wait (bounded; if it is late the result is the same)
        release.SetResult();
        var result = await job;

        Assert.True(result.Stopped);
        Assert.Empty(_rig.Resolver.Questions); // the stopped job asks nothing more
        Assert.Equal((0, 0, 1), (result.Copied, result.Skipped, result.Failed));
    }

    [Fact]
    public async Task SeveralInFlight_ProgressNeverGoesDown()
    {
        AddFiles(12);
        var progress = new ListProgress();

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", progress: progress);

        Assert.Equal(12, result.Copied);
        var reports = progress.Reports;
        Assert.Contains(reports, p => p.InFlight > 1);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].BytesDone >= reports[i - 1].BytesDone, $"BytesDone went {reports[i - 1].BytesDone} -> {reports[i].BytesDone} at report {i}.");
            Assert.True(reports[i].FilesDone >= reports[i - 1].FilesDone, $"FilesDone went {reports[i - 1].FilesDone} -> {reports[i].FilesDone} at report {i}.");
        }
    }

    // ---- helpers

    private async Task AssertOneAtATime(CopyOptions options)
    {
        AddFiles(4);
        var hold = new WriteHold(path => path.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase)); // every copy, not just the first
        _rig.Fs.BeforeWrite = hold.WaitAsync;

        var job = _rig.RunAsync([@"C:\src"], @"D:\dst", options);
        await hold.Entered(1).WaitAsync(Patience, Ct);

        // While the first copy is held, a second must never start. Nothing can be waited for here, so give it a short while to wrongly appear
        // (a bounded wait: it can only false-pass, never flake).
        var second = hold.Entered(2);
        var wrongly = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        Assert.NotSame(second, wrongly);
        Assert.Equal(1, hold.Count);
        Assert.Equal(1, PartsCreated());
        hold.Release();
        var result = await job;
        Assert.Equal(4, result.Copied);
    }

    /// <summary>f1.bin ... fN.bin, each a little bigger than the one before, so the planned order is f1 first.</summary>
    private void AddFiles(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Add($@"C:\src\f{i}.bin", 100 + i, seed: i);
        }
    }

    private void AssertSameAsSource(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Assert.Equal(_rig.Mem.ReadAllBytes($@"C:\src\f{i}.bin"), _rig.Mem.ReadAllBytes($@"D:\dst\src\f{i}.bin"));
        }
    }

    private int PartsCreated() =>
        _rig.Fs.Log.Count(l => l.StartsWith("CreateWrite ", StringComparison.Ordinal) && l.EndsWith(FileCopier.PartSuffix, StringComparison.OrdinalIgnoreCase));

    private string JournalText(Guid id)
    {
        using var stream = new FileStream(Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Populate(TreeCopierRig rig, bool existingTargets)
    {
        var files = new (string Path, int Size)[]
        {
            (@"C:\src\empty.txt", 0),
            (@"C:\src\a.txt", 10),
            (@"C:\src\b.txt", 5000),
            (@"C:\src\c.bin", 70_000),
            (@"C:\src\big.bin", 9 * MiB),
            (@"C:\src\sub\a.txt", 11),
            (@"C:\src\sub\d.bin", 300_000),
            (@"C:\src\sub\deep\e.txt", 12),
            (@"C:\src\sub\deep\f.bin", 2 * MiB),
        };
        var seed = 1;
        foreach (var (path, size) in files)
        {
            rig.Mem.AddFile(path, Bytes(size, seed++));
        }

        if (existingTargets)
        {
            rig.Mem.AddFile(@"D:\dst\src\a.txt", Bytes(3, 100));
            rig.Mem.AddFile(@"D:\dst\src\a (2).txt", Bytes(4, 101));
            rig.Mem.AddFile(@"D:\dst\src\sub\d.bin", Bytes(5, 102));
            rig.Mem.AddFile(@"D:\dst\src\sub\deep\f.bin", Bytes(6, 103));
        }
    }

    /// <summary>Every file under <paramref name="root"/>: relative path and content, sorted by path.</summary>
    private static List<(string Path, string Hex)> Tree(TreeCopierRig rig, string root)
    {
        var found = new List<(string, string)>();
        if (!rig.Mem.Exists(root))
        {
            return found;
        }

        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            foreach (var child in rig.Mem.EnumerateChildren(pending.Pop()))
            {
                if (child.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(child.FullPath);
                }
                else
                {
                    found.Add((RecordingFileSystem.Plain(child.FullPath)[root.Length..], Convert.ToHexString(rig.Mem.ReadAllBytes(child.FullPath))));
                }
            }
        }

        return [.. found.OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Holds every write that <c>matches</c> until <see cref="Release"/>; counts the files that reached a held write.</summary>
    private sealed class WriteHold(Func<string, bool> matches)
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _entered = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _entered.Count;
                }
            }
        }

        public async Task WaitAsync(string path, long offset, CancellationToken ct)
        {
            if (!matches(path))
            {
                return;
            }

            lock (_lock)
            {
                _entered.Add(RecordingFileSystem.Plain(path));
                foreach (var (count, signal) in _waiters)
                {
                    if (_entered.Count >= count)
                    {
                        signal.TrySetResult();
                    }
                }
            }

            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Completes once <paramref name="count"/> files are held (or were).</summary>
        public Task Entered(int count)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (_entered.Count >= count)
                {
                    signal.TrySetResult();
                }
                else
                {
                    _waiters.Add((count, signal));
                }
            }

            return signal.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    /// <summary>A clock that never moves; every timer fires at once, and the longest wait asked for is kept.</summary>
    private sealed class FrozenTime : TimeProvider
    {
        private long _longestTicks;

        public TimeSpan LongestDelay => TimeSpan.FromTicks(Interlocked.Read(ref _longestTicks));

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => 0;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                long seen;
                do
                {
                    seen = Interlocked.Read(ref _longestTicks);
                }
                while (dueTime.Ticks > seen && Interlocked.CompareExchange(ref _longestTicks, dueTime.Ticks, seen) != seen);

                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
