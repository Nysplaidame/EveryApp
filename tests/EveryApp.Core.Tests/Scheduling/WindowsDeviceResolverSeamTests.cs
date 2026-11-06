using EveryApp.Core.Scheduling;
using EveryApp.Core.Tests.Fakes;

namespace EveryApp.Core.Tests.Scheduling;

public sealed class WindowsDeviceResolverSeamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeVolumeApi : IVolumeApi
    {
        public Dictionary<string, string> Guids { get; } = new(StringComparer.OrdinalIgnoreCase); // "X:\" -> guid path
        public Dictionary<string, uint[]> Disks { get; } = new(StringComparer.OrdinalIgnoreCase); // guid path -> disk numbers
        public Dictionary<string, string> Mapped { get; } = new(StringComparer.OrdinalIgnoreCase); // "Z:" -> server ("" = unknown remote)
        public Dictionary<string, string> Subst { get; } = new(StringComparer.OrdinalIgnoreCase); // "S:" -> "\??\X:\dir"
        public int VolumeRootCalls { get; private set; }

        public string? GetMappedDriveServer(string driveLetter) => Mapped.TryGetValue(driveLetter, out var s) ? s : null;

        public string? GetSubstTarget(string driveLetter) => Subst.TryGetValue(driveLetter, out var s) ? s : null;

        public string GetVolumeRoot(string fullPath)
        {
            VolumeRootCalls++;
            return fullPath[..3];
        }

        public string? GetVolumeGuidPath(string root) => Guids.TryGetValue(root, out var g) ? g : null;

        public IReadOnlyList<uint> GetDiskNumbers(string guidPath) => Disks.TryGetValue(guidPath, out var d) ? d : [];
    }

    private static Dictionary<uint, uint[]> NoSpaces() => [];

    private static WindowsDeviceResolver Make(FakeVolumeApi api, Func<Dictionary<uint, uint[]>>? loader = null, TimeProvider? time = null, int waitMs = 100) =>
        new(api, loader ?? NoSpaces, time ?? TimeProvider.System, TimeSpan.FromMilliseconds(waitMs));

    private static string[] Ids(DeviceSet set) => [.. set.Ids.Order(StringComparer.Ordinal)];

    [Fact]
    public async Task WmiHang_DoesNotBlockScheduling_AndFallsBackToDiskNumber()
    {
        using var release = new ManualResetEventSlim();
        var api = new FakeVolumeApi();
        api.Guids[@"C:\"] = @"\\?\Volume{1}\";
        api.Disks[@"\\?\Volume{1}\"] = [9];
        var resolver = Make(api, () =>
        {
            release.Wait(); // wedged WMI
            return NoSpaces();
        });
        var scheduler = new DriveAwareScheduler(resolver);

        try
        {
            await using var lease = await scheduler.AcquireAsync(@"C:\a", @"C:\b", Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
            Assert.Equal(["disk:9"], Ids(resolver.Resolve(@"C:\a")));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void WmiHang_ResultIsNotCached_LateSuccessIsPickedUp()
    {
        using var release = new ManualResetEventSlim();
        var api = new FakeVolumeApi();
        api.Guids[@"C:\"] = @"\\?\Volume{1}\";
        api.Disks[@"\\?\Volume{1}\"] = [9];
        var resolver = Make(api, () =>
        {
            release.Wait();
            return new Dictionary<uint, uint[]> { [9] = [6, 7] };
        });

        Assert.Equal(["disk:9"], Ids(resolver.Resolve(@"C:\x")));

        release.Set();
        SpinWait.SpinUntil(() => Ids(resolver.Resolve(@"C:\x")).Length == 3, TimeSpan.FromSeconds(5));

        Assert.Equal(["disk:6", "disk:7", "disk:9"], Ids(resolver.Resolve(@"C:\x")));
    }

    [Fact]
    public void WmiFailure_IsNotCached_RetriedAfterBackoff()
    {
        var time = new FakeTime();
        var calls = 0;
        var api = new FakeVolumeApi();
        api.Guids[@"C:\"] = @"\\?\Volume{1}\";
        api.Disks[@"\\?\Volume{1}\"] = [9];
        var resolver = Make(api, () =>
        {
            return ++calls == 1 ? throw new InvalidOperationException("wmi down") : new Dictionary<uint, uint[]> { [9] = [6, 7] };
        }, time);

        Assert.Equal(["disk:9"], Ids(resolver.Resolve(@"C:\x")));
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(["disk:9"], Ids(resolver.Resolve(@"C:\x")));
        Assert.Equal(1, calls); // still backing off

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(["disk:6", "disk:7", "disk:9"], Ids(resolver.Resolve(@"C:\x")));
        Assert.Equal(2, calls);

        Assert.Equal(["disk:6", "disk:7", "disk:9"], Ids(resolver.Resolve(@"C:\x")));
        Assert.Equal(2, calls); // success is cached
    }

    [Fact]
    public void SameLetter_DifferentVolumeGuid_ReResolves()
    {
        var api = new FakeVolumeApi();
        api.Guids[@"E:\"] = @"\\?\Volume{usb-one}\";
        api.Disks[@"\\?\Volume{usb-one}\"] = [4];
        api.Disks[@"\\?\Volume{usb-two}\"] = [7];
        var resolver = Make(api);

        Assert.Equal(["disk:4"], Ids(resolver.Resolve(@"E:\a")));

        api.Guids[@"E:\"] = @"\\?\Volume{usb-two}\"; // a different drive plugged in under the same letter

        Assert.Equal(["disk:7"], Ids(resolver.Resolve(@"E:\a")));
    }

    [Fact]
    public void SubstDrive_ResolvesToBackingDisk()
    {
        var api = new FakeVolumeApi();
        api.Guids[@"D:\"] = @"\\?\Volume{d}\";
        api.Disks[@"\\?\Volume{d}\"] = [3];
        api.Subst["S:"] = @"\??\D:\projects";
        var resolver = Make(api);

        Assert.Equal(["disk:3"], Ids(resolver.Resolve(@"S:\file.txt")));
        Assert.True(resolver.Resolve(@"S:\x").Overlaps(resolver.Resolve(@"D:\y")));
    }

    [Fact]
    public void SubstDrive_ToUncPath_ResolvesToServer()
    {
        var api = new FakeVolumeApi();
        api.Subst["S:"] = @"\??\UNC\NAS\share\dir";
        var resolver = Make(api);

        Assert.Equal(["smb:nas"], Ids(resolver.Resolve(@"S:\file.txt")));
    }

    [Fact]
    public void SubstLoop_DoesNotRecurseForever()
    {
        var api = new FakeVolumeApi();
        api.Subst["S:"] = @"\??\T:\x";
        api.Subst["T:"] = @"\??\S:\x";
        var resolver = Make(api);

        Assert.NotEmpty(resolver.Resolve(@"S:\f").Ids);
    }

    [Fact]
    public void MappedDrive_ResolvesToServer_WithoutTouchingTheVolume()
    {
        var api = new FakeVolumeApi();
        api.Mapped["Z:"] = "NAS";
        api.Mapped["Y:"] = ""; // remote but name unknown
        var resolver = Make(api);

        Assert.Equal(["smb:nas"], Ids(resolver.Resolve(@"Z:\folder\file")));
        Assert.Equal(["smb:nas"], Ids(resolver.Resolve(@"\\?\Z:\folder\file")));
        Assert.Single(resolver.Resolve(@"Y:\a").Ids);
        Assert.Equal(0, api.VolumeRootCalls);
    }
}
