using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Scheduling;
using EveryApp.Core.Tests.Fakes;

namespace Ferry.Engine.Tests;

/// <summary>Tree copies on the real disk: permissions, extra data streams, block cloning (ReFS only) and the real environment seams.</summary>
public sealed class TreeCopierRealFileTests : IDisposable
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ferry-tree-" + Guid.NewGuid().ToString("N"));
    private readonly string _jobs;

    public TreeCopierRealFileTests()
    {
        Directory.CreateDirectory(_root);
        _jobs = Path.Combine(_root, "jobs");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task<CopyResult> Run(string source, string destination, CopyOptions options, IFileSystem? fs = null, string? jobs = null) =>
        new TreeCopier(fs ?? new WindowsFileSystem(), new DriveAwareScheduler(new FakeDeviceResolver()), new ScriptedResolver(), jobs ?? _jobs, new FakeSleepBlocker(), null, null, new FakeElevation())
            .RunAsync(Guid.NewGuid(), [source], destination, CopyMode.Copy, options, new PauseGate(), new ListProgress(), Ct);

    private static bool HasExplicitEveryoneRule(string file) =>
        new FileInfo(file).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(Everyone) && r.FileSystemRights.HasFlag(FileSystemRights.ReadExtendedAttributes));

    private string SourceWithExplicitAce()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var file = Path.Combine(src, "a.txt");
        File.WriteAllText(file, "secret");
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.ReadExtendedAttributes, AccessControlType.Allow));
        info.SetAccessControl(security);
        return src;
    }

    [Fact]
    public async Task Run_RealFiles_CopiesTree()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        Directory.CreateDirectory(Path.Combine(src, "sub", "empty"));
        var big = new byte[9 * 1024 * 1024 + 123]; // over the unbuffered threshold
        new Random(5).NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "sub", "big.bin"), big);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

        var result = await Run(src, Path.Combine(_root, "dst"), new CopyOptions());

        Assert.Equal(2, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_root, "dst", "src", "sub", "big.bin")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "dst", "src", "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "dst", "src", "sub", "empty")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "dst"), "*.ferrypart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopyPermissions_On_CopiesAcl()
    {
        var src = SourceWithExplicitAce();

        var result = await Run(src, Path.Combine(_root, "dst"), new CopyOptions { CopyPermissions = true });

        Assert.Equal(1, result.Copied);
        Assert.Empty(result.Problems);
        Assert.True(HasExplicitEveryoneRule(Path.Combine(_root, "dst", "src", "a.txt")));
    }

    [Fact]
    public async Task CopyPermissions_Off_InheritsDestinationAcl()
    {
        var src = SourceWithExplicitAce();

        var result = await Run(src, Path.Combine(_root, "dst"), new CopyOptions { CopyPermissions = false });

        Assert.Equal(1, result.Copied);
        var copy = Path.Combine(_root, "dst", "src", "a.txt");
        Assert.False(HasExplicitEveryoneRule(copy));
        var rules = new FileInfo(copy).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.True(r.IsInherited, "Without the option the copy only has what the destination folder passes down."));
    }

    [Fact]
    public async Task AlternateStreams_OptIn_Copied()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "main");
        File.WriteAllText(Path.Combine(src, "a.txt") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllText(Path.Combine(src, "a.txt") + ":other", "x");

        var on = await Run(src, Path.Combine(_root, "on"), new CopyOptions { CopyAlternateStreams = true });
        var off = await Run(src, Path.Combine(_root, "off"), new CopyOptions());

        Assert.Equal(1, on.Copied);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Path.Combine(_root, "on", "src", "a.txt") + ":Zone.Identifier"));
        Assert.Equal("x", File.ReadAllText(Path.Combine(_root, "on", "src", "a.txt") + ":other"));
        Assert.Equal(1, off.Copied);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Path.Combine(_root, "off", "src", "a.txt") + ":Zone.Identifier")); // always kept
        Assert.False(File.Exists(Path.Combine(_root, "off", "src", "a.txt") + ":other"));
    }

    [Fact]
    [Trait("Category", "Machine")]
    public async Task ReFS_SameVolume_UsesBlockClone()
    {
        var refs = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(refs is null, "No ReFS volume on this PC.");
        var root = Path.Combine(refs!.RootDirectory.FullName, "ferry-refs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
            var data = new byte[3 * 1024 * 1024 + 17];
            new Random(9).NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, "a.bin"), data);
            var fs = new RecordingFileSystem(new WindowsFileSystem());

            var result = await Run(src, Path.Combine(root, "dst"), new CopyOptions(), fs, Path.Combine(root, "jobs"));

            Assert.Equal(1, result.Copied);
            Assert.True(result.AllVerified);
            Assert.Contains(fs.Log, l => l.StartsWith("Clone ", StringComparison.Ordinal) && l.EndsWith(" ok", StringComparison.Ordinal));
            Assert.Equal(0, fs.Count("CreateWrite"));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(root, "dst", "src", "a.bin")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void WindowsDestinationInfo_ReadsFileSystem_AndTreatsSharesAsSmb()
    {
        var info = new WindowsDestinationInfo();
        Assert.Equal(new DriveInfo(Path.GetPathRoot(_root)!).DriveFormat, info.FileSystemOf(_root));
        Assert.Equal(new DriveInfo(Path.GetPathRoot(_root)!).DriveFormat, info.FileSystemOf(@"\\?\" + _root));
        Assert.Equal("SMB", info.FileSystemOf(@"\\server\share\folder"));
        Assert.Equal("SMB", info.FileSystemOf(@"\\?\UNC\server\share\folder"));
        Assert.Equal(string.Empty, info.FileSystemOf("relative"));
        _ = info.LongPathsEnabled; // reads the registry without throwing
    }

    [Fact]
    public void WindowsSleepBlocker_AcquireAndRelease()
    {
        using (WindowsSleepBlocker.Instance.Acquire())
        {
            Assert.True(EveryApp.Core.Power.KeepAwake.ActiveHolds >= 1);
        }
    }

    // ---- Several files at once on the real disk (solid-state forced, four workers)

    private const int SmallFiles = 300;
    private const int LargeFiles = 3;

    /// <summary>A source folder with many small files (1 to 64 KiB) and a few large ones (12 MiB, past the unbuffered threshold).</summary>
    private string ManySmallAndSomeLargeSource()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var random = new Random(11);
        for (var i = 0; i < SmallFiles; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(src, "d" + (i % 10).ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            var data = new byte[random.Next(1, 64 * 1024 + 1)];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(folder, $"small{i}.bin"), data);
        }

        for (var i = 0; i < LargeFiles; i++)
        {
            var data = new byte[12 * 1024 * 1024];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, $"large{i}.bin"), data);
        }

        return src;
    }

    private static async Task<(CopyResult Result, ListProgress Progress)> RunParallel(string source, string destination, CopyMode mode, string jobs, IFileSystem? fs = null)
    {
        var progress = new ListProgress();
        var result = await new TreeCopier(
                fs ?? new WindowsFileSystem(), new DriveAwareScheduler(new FakeDeviceResolver()), new ScriptedResolver(), jobs, new FakeSleepBlocker(), null, null,
                new FakeElevation(), new FakeDriveKindProbe { Kind = DriveKind.SolidState }, workers: 4)
            .RunAsync(Guid.NewGuid(), [source], destination, mode, new CopyOptions { Verify = true }, new PauseGate(), progress, Ct);
        return (result, progress);
    }

    private static void AssertSameTree(string source, string copy)
    {
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(SmallFiles + LargeFiles, sourceFiles.Count);
        foreach (var file in sourceFiles)
        {
            var other = Path.Combine(copy, Path.GetRelativePath(source, file));
            Assert.True(File.Exists(other), "Missing copy of " + Path.GetRelativePath(source, file));
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(other)), "Different data in " + Path.GetRelativePath(source, file));
        }

        Assert.Empty(Directory.EnumerateFiles(copy, "*.ferrypart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Parallel_ManySmallAndSomeLarge_VerifyOn_AllMatch()
    {
        var src = ManySmallAndSomeLargeSource();
        var dst = Path.Combine(_root, "dst");

        var (result, progress) = await RunParallel(src, dst, CopyMode.Copy, _jobs);

        Assert.Equal(SmallFiles + LargeFiles, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Empty(result.Problems);
        Assert.Contains(progress.Reports, r => r.InFlight > 1); // really ran several at once
        AssertSameTree(src, Path.Combine(dst, "src"));
    }

    [Fact]
    public async Task Parallel_ManySmallAndSomeLarge_Move_SourcesGoneDestinationsEqual()
    {
        var src = ManySmallAndSomeLargeSource();
        var dst = Path.Combine(_root, "dst");
        var expected = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(src, f), File.ReadAllBytes);

        // Both folders sit on one real volume, where a move is a single rename; call the source another volume so every file is copied, checked, then deleted.
        var fs = new RecordingFileSystem(new WindowsFileSystem())
        {
            VolumeIdOverride = path => path.StartsWith(src, StringComparison.OrdinalIgnoreCase) ? "other-volume" : null,
        };

        var (result, progress) = await RunParallel(src, dst, CopyMode.Move, _jobs, fs);

        Assert.Equal(SmallFiles + LargeFiles, result.Copied);
        Assert.True(result.AllVerified);
        Assert.Empty(result.Problems);
        Assert.Contains(progress.Reports, r => r.InFlight > 1);
        Assert.True(!Directory.Exists(src) || !Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Any(), "Moved files must be gone from the source.");
        foreach (var (relative, data) in expected)
        {
            Assert.True(data.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dst, "src", relative))), "Different data in " + relative);
        }

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dst, "src"), "*.ferrypart", SearchOption.AllDirectories));
    }

    // ---- N2: a link inside the destination cannot redirect what resume deletes or rewrites

    private static void MakeJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "Could not create a junction for the test.");
    }

    /// <summary>A saved job with one part record; the part and its target sit in <paramref name="folder"/> of the destination.</summary>
    private Guid SavedJobWithPartRecord(string src, string destination, string folder)
    {
        var id = Guid.NewGuid();
        var spec = new JobSpec(JobSpec.CurrentVersion, [src], destination, CopyMode.Copy, new CopyOptions());
        Directory.CreateDirectory(_jobs);
        using var journal = JobJournal.Create(_jobs, id, spec.ToJson());
        journal.Append(new JournalRecord(
            JournalRecordKind.FilePartStarted, @"0\src\sub\a.txt", 5, DateTime.UtcNow, null,
            folder + @"\a.txt.0123abcd.ferrypart", folder + @"\a.txt"));
        return id;
    }

    private Task<CopyResult> ResumeJob(Guid id) =>
        new TreeCopier(new WindowsFileSystem(), new DriveAwareScheduler(new FakeDeviceResolver()), new ScriptedResolver(), _jobs, new FakeSleepBlocker(), null, null, new FakeElevation())
            .ResumeAsync(id, new FakeConfirmation(), new PauseGate(), new ListProgress(), Ct);

    [Fact]
    public async Task Resume_PartFileBehindADestinationJunction_IsNotDeleted()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "x.txt"), "x");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        MakeJunction(Path.Combine(destination, "evil"), outside);
        var outsidePart = Path.Combine(outside, "a.txt.0123abcd.ferrypart");
        File.WriteAllText(outsidePart, "not ours to delete");
        var id = SavedJobWithPartRecord(src, destination, "evil");

        await ResumeJob(id);

        Assert.True(File.Exists(outsidePart));
    }

    [Fact]
    public async Task Resume_RecoveredTargetBehindADestinationJunction_IsNotTouched()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var sourceFile = Path.Combine(src, "sub", "a.txt");
        File.WriteAllText(sourceFile, "hello");
        File.SetLastWriteTimeUtc(sourceFile, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dst")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        MakeJunction(Path.Combine(destination, "evil"), outside); // the saved job points the finished name into a link
        var outsideFile = Path.Combine(outside, "a.txt");
        File.WriteAllText(outsideFile, "hello"); // same data, so it looks like a finished copy
        var stamp = new DateTime(2020, 6, 6, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(outsideFile, stamp);
        var id = SavedJobWithPartRecord(src, destination, "evil");

        await ResumeJob(id);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(outsideFile));
    }
}
