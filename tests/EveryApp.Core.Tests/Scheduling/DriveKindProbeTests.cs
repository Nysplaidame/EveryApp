using EveryApp.Core.Scheduling;

namespace EveryApp.Core.Tests.Scheduling;

public sealed class DriveKindProbeTests
{
    private sealed class FakeVolumeApi : IVolumeApi
    {
        public Dictionary<uint, bool?> SeekPenalty { get; } = [];
        public Dictionary<uint, int> Queries { get; } = [];

        public string? GetMappedDriveServer(string driveLetter) => null;

        public string? GetSubstTarget(string driveLetter) => null;

        public string GetVolumeRoot(string fullPath) => fullPath[..3];

        public string? GetVolumeGuidPath(string root) => null;

        public IReadOnlyList<uint> GetDiskNumbers(string guidPath) => [];

        public bool? HasSeekPenalty(uint diskNumber)
        {
            Queries[diskNumber] = Queries.GetValueOrDefault(diskNumber) + 1;
            return SeekPenalty.TryGetValue(diskNumber, out var v) ? v : null;
        }
    }

    [Fact]
    public void AllDisksNoSeekPenalty_IsSolidState()
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = false;
        api.SeekPenalty[1] = false;

        Assert.Equal(DriveKind.SolidState, new WindowsDriveKindProbe(api).KindOf(DeviceSet.Of("disk:0", "disk:1")));
    }

    [Fact]
    public void AnyDiskSpinning_IsSpinning()
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = false;
        api.SeekPenalty[1] = true;

        Assert.Equal(DriveKind.Spinning, new WindowsDriveKindProbe(api).KindOf(DeviceSet.Of("disk:0", "disk:1")));
    }

    [Fact]
    public void QueryFails_IsUnknown()
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = false;
        api.SeekPenalty[1] = null;

        Assert.Equal(DriveKind.Unknown, new WindowsDriveKindProbe(api).KindOf(DeviceSet.Of("disk:0", "disk:1")));
    }

    [Fact]
    public void EmptySet_IsUnknown()
    {
        Assert.Equal(DriveKind.Unknown, new WindowsDriveKindProbe(new FakeVolumeApi()).KindOf(DeviceSet.Of()));
    }

    [Theory]
    [InlineData("smb:server")]
    [InlineData("vol:{1234}")]
    [InlineData("unresolved:x")]
    public void NetworkOrVolumeId_IsUnknown(string id)
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = false;

        Assert.Equal(DriveKind.Unknown, new WindowsDriveKindProbe(api).KindOf(DeviceSet.Of("disk:0", id)));
    }

    [Fact]
    public void Answers_AreCachedPerDisk()
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = false;
        var probe = new WindowsDriveKindProbe(api);

        probe.KindOf(DeviceSet.Of("disk:0"));
        probe.KindOf(DeviceSet.Of("disk:0"));

        Assert.Equal(1, api.Queries[0]);
    }

    [Fact]
    public void FailedAnswers_AreNotCached()
    {
        var api = new FakeVolumeApi();
        api.SeekPenalty[0] = null;
        var probe = new WindowsDriveKindProbe(api);

        probe.KindOf(DeviceSet.Of("disk:0"));
        probe.KindOf(DeviceSet.Of("disk:0"));

        Assert.Equal(2, api.Queries[0]);
    }

    [Fact]
    public void RealSystemDrive_DoesNotThrow()
    {
        var kind = WindowsDriveKindProbe.Instance.KindOf(new WindowsDeviceResolver().Resolve(Environment.SystemDirectory));

        Assert.True(Enum.IsDefined(kind));
    }
}
