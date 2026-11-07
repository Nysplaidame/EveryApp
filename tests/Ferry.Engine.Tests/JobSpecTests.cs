using EveryApp.Core.Hashing;

namespace Ferry.Engine.Tests;

/// <summary>The job description stored as the journal header must survive a round trip with every option set away from its default.</summary>
public sealed class JobSpecTests
{
    private static CopyOptions EveryOptionChanged() => new()
    {
        Verify = false,
        KeepDatesAndAttributes = false,
        CopyPermissions = true,
        IfExists = ConflictRule.OverwriteIfNewer,
        IfUnreadable = FailureRule.Stop,
        RescueMode = true,
        FollowReparsePoints = true,
        SpeedLimitBytesPerSecond = 12_345_678,
        Exclude = ["**/node_modules/**", "Thumbs.db"],
        DryRun = true,
        HashAlgorithm = HashAlgorithmKind.Sha256,
        BufferSizeBytes = 1024 * 1024,
        CopyAlternateStreams = true,
        ExportChecksums = true,
    };

    [Fact]
    public void EveryOption_InTheTestDiffersFromItsDefault()
    {
        // Guards this test: a new CopyOptions field must be added to EveryOptionChanged() too.
        var changed = EveryOptionChanged();
        var defaults = new CopyOptions();
        foreach (var property in typeof(CopyOptions).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
        {
            var a = property.GetValue(changed);
            var b = property.GetValue(defaults);
            var same = a is IEnumerable<string> x && b is IEnumerable<string> y ? x.SequenceEqual(y) : Equals(a, b);
            Assert.False(same, $"CopyOptions.{property.Name} is still at its default in this test.");
        }
    }

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var spec = new JobSpec(JobSpec.CurrentVersion, [@"C:\a", @"E:\b\c.txt"], @"D:\dst", CopyMode.Move, EveryOptionChanged());

        var back = JobSpec.Parse(spec.ToJson());

        Assert.Equal(spec.Version, back.Version);
        Assert.Equal(spec.Sources, back.Sources);
        Assert.Equal(spec.Destination, back.Destination);
        Assert.Equal(spec.Mode, back.Mode);
        Assert.Equal(spec.Options.Exclude, back.Options.Exclude);
        Assert.Equal(spec.Options with { Exclude = [] }, back.Options with { Exclude = [] });
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Version":1,"Sources":["relative"],"Destination":"D:\\dst","Mode":"Copy","Options":{}}""")]
    [InlineData("""{"Version":1,"Sources":["C:\\a"],"Destination":"D:\\dst","Mode":"Copy","Options":{"BufferSizeBytes":-1}}""")]
    public void Parse_Damaged_Throws(string json) => Assert.Throws<InvalidDataException>(() => JobSpec.Parse(json));
}
