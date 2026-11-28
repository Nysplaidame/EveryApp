using EveryApp.Core.Scheduling;

namespace EveryApp.Core.Tests.Scheduling;

public sealed class WindowsDeviceResolverTests
{
    private readonly WindowsDeviceResolver _resolver = new();

    [Theory]
    [InlineData(@"\\NAS\Share\folder\file.bin", "smb:nas")]
    [InlineData(@"\\?\UNC\NAS\Share\folder\file.bin", "smb:nas")]
    [InlineData(@"\\Server01\x", "smb:server01")]
    [InlineData(@"//Server01/x/y", "smb:server01")]
    public void UncPaths_MapToLowercasedServer(string path, string expected)
    {
        var set = _resolver.Resolve(path);

        Assert.Equal([expected], set.Ids);
    }

    [Fact]
    public void SameServerDifferentShares_Overlap()
    {
        Assert.True(_resolver.Resolve(@"\\nas\a\x").Overlaps(_resolver.Resolve(@"\\?\UNC\NAS\b\y")));
        Assert.False(_resolver.Resolve(@"\\nas\a").Overlaps(_resolver.Resolve(@"\\other\a")));
    }

    [Fact]
    public void LocalPath_ResolvesToSomething_PlainAndCanonicalFormsAgree()
    {
        var temp = Path.GetTempPath();

        var plain = _resolver.Resolve(temp);
        var canonical = _resolver.Resolve(@"\\?\" + temp);

        Assert.NotEmpty(plain.Ids);
        Assert.Equal(plain.Ids.Order(), canonical.Ids.Order());
    }

    [Fact]
    public void NonexistentPathOnExistingDrive_StillResolves()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "not", "there.bin");

        var set = _resolver.Resolve(path);

        Assert.Equal(_resolver.Resolve(Path.GetTempPath()).Ids.Order(), set.Ids.Order());
    }

    [Fact]
    public void GarbagePath_NeverThrows_AndStillGivesAnId()
    {
        foreach (var path in new[] { "", "   ", "\0", @"\\?\", @"\\", @"Q:\definitely\missing\drive?", "relative\\thing.txt" })
        {
            var set = _resolver.Resolve(path);
            Assert.NotEmpty(set.Ids);
        }
    }

    /// <summary>
    /// Local only (excluded from CI by build.ps1). Prints the device set of every fixed drive so it can be compared
    /// with Disk Management. Run: dotnet test --solution EveryApp.slnx -- --filter-trait "Category=Machine"
    /// </summary>
    [Fact]
    [Trait("Category", "Machine")]
    public void Machine_PrintsDeviceSetForEveryFixedDrive()
    {
        var lines = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var set = _resolver.Resolve(drive.RootDirectory.FullName);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

            lines.Add($"{drive.Name} [{drive.DriveFormat}, {drive.VolumeLabel}] -> {string.Join(", ", set.Ids.Order(StringComparer.Ordinal))}  ({elapsed.TotalMilliseconds:F0} ms)");
            Assert.NotEmpty(set.Ids);
        }

        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);
        TestContext.Current.SendDiagnosticMessage(report);
        var outFile = Environment.GetEnvironmentVariable("EVERYAPP_MACHINE_TEST_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            File.WriteAllText(outFile, report);
        }
    }
}
