using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: security review fixes (round 2): elevation, links in the destination, verified deletes, tampered journals, hostile trees.</summary>
public sealed class TreeCopierSecurityTests : TreeCopierTestBase
{
    // ---- F1: never run elevated, never advise elevating

    [Fact]
    public async Task Elevated_RunRefused_NothingWritten()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Elevation.IsElevated = true;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.True(result.Stopped);
        Assert.Equal(CopyMessages.RunningElevated, Assert.Single(result.Problems).Message);
        Assert.Empty(_rig.Fs.Log);
        Assert.False(_rig.AnyJournal());
    }

    [Fact]
    public async Task Elevated_ResumeRefused_JournalUntouched()
    {
        AddText(@"C:\src\a.txt", "a");
        var id = Guid.NewGuid();
        _rig.Faults.FailRead(@"C:\src\a.txt", 0, IoErrorKind.AccessDenied, times: 1);
        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);
        var before = File.ReadAllBytes(Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal"));
        _rig.Elevation.IsElevated = true;
        _rig.Fs.ClearLog();

        var result = await _rig.ResumeAsync(id);

        Assert.Equal(CopyMessages.RunningElevated, Assert.Single(result.Problems).Message);
        Assert.Empty(_rig.Fs.Log);
        Assert.Empty(_rig.Confirmation.Asked);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_rig.JobsDir, id.ToString("N") + ".journal")));
    }

    [Fact]
    public void NoMessage_AdvisesRunningAsAdministrator()
    {
        var messages = typeof(CopyMessages).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();
        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            Assert.DoesNotMatch(new Regex(@"(?i)(restart|re-?run|run (it|this|the job|ferry)|start (it|ferry)|right-click)[^.]*as (an )?administrator"), message);
        }

        Assert.DoesNotContain("as administrator", CopyMessages.OwnerNotKept, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsElevationCheck_Reads()
    {
        _ = WindowsElevationCheck.Instance.IsElevated; // must not throw
    }

    // ---- F2: links in the destination

    [Fact]
    public async Task ExistingDestinationFolderIsALink_Skipped_NothingWrittenThroughIt()
    {
        AddText(@"C:\src\sub\a.txt", "a");
        _rig.Mem.CreateDirectory(@"D:\elsewhere");
        _rig.Mem.AddJunction(@"D:\dst\src\sub", @"D:\elsewhere");

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Failed);
        Assert.Equal(CopyMessages.DestinationFolderIsLink, Assert.Single(result.Problems).Message);
        Assert.Empty(_rig.Mem.EnumerateChildren(@"D:\elsewhere"));
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
    }

    [Fact]
    public async Task PartFileLandsOutsideTheDestination_DeletedThroughItsHandle_FileFails()
    {
        AddText(@"C:\src\sub\a.txt", "a");
        _rig.Mem.CreateDirectory(@"D:\outside");
        _rig.Fs.BeforeCreateWrite = path =>
        {
            // The folder was checked and created; now it is swapped for a link to somewhere else before the part file is made.
            if (_rig.Mem.Exists(@"D:\dst\src\sub") && !_rig.Mem.GetEntry(@"D:\dst\src\sub").IsReparsePoint)
            {
                _rig.Mem.Delete(@"D:\dst\src\sub");
                _rig.Mem.AddJunction(@"D:\dst\src\sub", @"D:\outside");
            }
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.Equal(1, result.Failed);
        Assert.Equal(CopyMessages.PartOutsideDestination, Assert.Single(result.Problems).Message);
        Assert.Empty(_rig.Mem.EnumerateChildren(@"D:\outside"));
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("Delete ", StringComparison.Ordinal) && l.EndsWith(".ferrypart", StringComparison.Ordinal));
    }

    // ---- F3: deletes only what was checked

    [Fact]
    public async Task Move_SourceReplacedBeforeDelete_Kept()
    {
        AddText(@"C:\src\a.txt", "original");
        _rig.Fs.BeforeDeleteVerified = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"C:\src\a.txt", StringComparison.OrdinalIgnoreCase))
            {
                _rig.Mem.Delete(@"C:\src\a.txt");
                AddText(@"C:\src\a.txt", "someone else's"); // same name, same size, different file
            }
        };

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move);

        Assert.Equal(CopyMessages.OriginalChanged, Assert.Single(result.Problems).Message);
        Assert.Equal("someone else's", Text(@"C:\src\a.txt"));
        Assert.Equal("original", Text(@"D:\dst\src\a.txt"));
        Assert.DoesNotContain(_rig.Fs.Log, l => l.StartsWith("Delete C:", StringComparison.Ordinal)); // never by name
    }

    [Fact]
    public async Task Move_EmptiedFolderReplaced_NotRemoved()
    {
        AddText(@"C:\src\sub\a.txt", "a");
        _rig.Fs.BeforeDeleteVerified = path =>
        {
            if (RecordingFileSystem.Plain(path).Equals(@"C:\src\sub", StringComparison.OrdinalIgnoreCase))
            {
                _rig.Mem.Delete(@"C:\src\sub");
                _rig.Mem.CreateDirectory(@"C:\src\sub"); // a different (empty) folder now has the name
            }
        };

        await _rig.RunAsync([@"C:\src"], @"D:\dst", mode: CopyMode.Move);

        Assert.True(_rig.Mem.Exists(@"C:\src\sub"));
        Assert.Contains(@"DeleteFolderVerified C:\src\sub", _rig.Fs.Log);
    }

    // ---- F8: tampered journal, confirmation

    [Fact]
    public async Task Resume_NotConfirmed_DoesNothing()
    {
        AddText(@"C:\src\a.txt", "a");
        var id = Guid.NewGuid();
        _rig.Faults.FailRead(@"C:\src\a.txt", 0, IoErrorKind.AccessDenied, times: 1);
        await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { IfUnreadable = FailureRule.Stop }, jobId: id);
        _rig.Fs.ClearLog();
        _rig.Confirmation.Answer = false;

        var result = await _rig.ResumeAsync(id);

        Assert.True(result.Stopped);
        Assert.Equal(CopyMessages.ResumeNotConfirmed, Assert.Single(result.Problems).Message);
        Assert.Empty(_rig.Fs.Log);
        var asked = Assert.Single(_rig.Confirmation.Asked);
        Assert.Equal([@"C:\src"], asked.Sources);
        Assert.Equal(@"D:\dst", asked.Destination);
        Assert.Equal(CopyMode.Copy, asked.Mode);
    }

    [Fact]
    public async Task Resume_PartRecordWithoutRandomSuffix_NotDeleted()
    {
        // A journal that passes the format rules but names a file Ferry would never have made: it must not be deleted.
        AddText(@"C:\src\a.txt", "a");
        AddText(@"D:\dst\src\a.txt.ferrypart", "someone's file");
        var id = Guid.NewGuid();
        var spec = new JobSpec(JobSpec.CurrentVersion, [@"C:\src"], @"D:\dst", CopyMode.Copy, new CopyOptions { IfExists = ConflictRule.Skip });
        Directory.CreateDirectory(_rig.JobsDir);
        using (var journal = JobJournal.Create(_rig.JobsDir, id, spec.ToJson()))
        {
            journal.Append(new JournalRecord(JournalRecordKind.FilePartStarted, @"0\src\a.txt", 1, DateTime.UnixEpoch, null, @"src\a.txt.ferrypart", @"src\a.txt"));
        }

        await _rig.ResumeAsync(id);

        Assert.Equal("someone's file", Text(@"D:\dst\src\a.txt.ferrypart"));
    }

    [Theory]
    [InlineData(@"C:\dst", @"a\b.txt", true)]
    [InlineData(@"C:\dst", @"..\x.txt", false)]
    [InlineData(@"C:\dst", @"a\..\..\x.txt", false)]
    [InlineData(@"C:\dst", @"C:\x.txt", false)]
    [InlineData(@"C:\dst", @"\x.txt", false)]
    [InlineData(@"\\?\C:\dst", @"a.txt", true)]
    public void JournalPaths_MustStayInsideTheRoot(string root, string relative, bool inside) =>
        Assert.Equal(inside, TreePaths.TryContain(root, relative, out _));

    [Theory]
    [InlineData("a.txt.0011aabb.ferrypart", true)]
    [InlineData("a.txt.ferrypart", false)]
    [InlineData("a.txt.0011AABB.ferrypart", false)]
    [InlineData("a.txt.0011aab.ferrypart", false)]
    [InlineData(".0011aabb.ferrypart", false)]
    public void PartNames_MustLookLikeOurs(string name, bool ours) => Assert.Equal(ours, ResumeRecovery.LooksLikeOurPart(name));

    // ---- F9: hostile trees

    [Fact]
    public async Task JournalDriveNearlyFull_Refused_NothingWritten()
    {
        AddText(@"C:\src\a.txt", "a");
        _rig.Destination.JobsFreeBytes = 10L * 1024 * 1024;

        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst");

        Assert.True(result.Stopped);
        Assert.Contains("50 MB", Assert.Single(result.Problems).Message, StringComparison.Ordinal);
        Assert.Equal(0, _rig.Fs.Count("CreateWrite"));
        Assert.False(_rig.AnyJournal());
    }

    [Fact]
    public void Problems_AreCapped_WithACountOfTheRest()
    {
        var outcome = new JobOutcome(verifyOn: true, maxProblems: 3);
        for (var i = 0; i < 5; i++)
        {
            outcome.Problems.Add(new Problem($"p{i}", IoErrorKind.Other, "x"));
        }

        var result = outcome.ToResult(stopped: false);

        Assert.Equal(["p0", "p1", "p2"], result.Problems.Select(p => p.Path));
        Assert.Equal(2, result.MoreProblems);
    }

    [Fact]
    public async Task HugeTree_PlansQuickly()
    {
        for (var d = 0; d < 20_000; d++)
        {
            for (var f = 0; f < 5; f++)
            {
                _rig.Mem.AddFile($@"C:\src\d{d / 100}\d{d}\f{f}.txt", [1]);
            }
        }

        var watch = Stopwatch.StartNew();
        var result = await _rig.RunAsync([@"C:\src"], @"D:\dst", new CopyOptions { DryRun = true });
        watch.Stop();

        Assert.Equal(100_000, result.Copied);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Planning took {watch.Elapsed.TotalSeconds:0.00} s.");
    }

    // ---- N3: the elevation check cannot be swapped out by a host

    [Fact]
    public void NoPublicConstructor_AcceptsAnElevationCheck() =>
        Assert.All(
            typeof(TreeCopier).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            c => Assert.DoesNotContain(c.GetParameters(), p => typeof(IElevationCheck).IsAssignableFrom(p.ParameterType)));
}
