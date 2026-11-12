using System.Security.Cryptography;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Preflight;

namespace Ferry.Engine.Tests;

/// <summary>Tree copier: Dry run, pre-flight checks and source/destination overlap.</summary>
public sealed class TreeCopierPlanTests : TreeCopierTestBase
{
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
}
