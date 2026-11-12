using EveryApp.Core.Paths;

namespace EveryApp.Core.Tests.Paths;

public sealed class PathSafetyTests
{
    [Fact]
    public void IsWithin_RejectsDotDotEscape() => Assert.False(PathSafety.IsWithin(@"C:\a", @"C:\a\..\b"));

    [Fact]
    public void IsWithin_RejectsPrefixTrick() => Assert.False(PathSafety.IsWithin(@"C:\a", @"C:\ab\x"));

    [Theory]
    [InlineData(@"C:\a", @"C:\a\b\c")]
    [InlineData(@"C:\a", @"c:\A\B")]
    [InlineData(@"C:\a\", @"C:\a\b")]
    [InlineData(@"C:\a", @"C:\a\x\..\y")]
    [InlineData(@"C:\a", @"\\?\C:\a\b")]
    [InlineData(@"\\?\C:\a", @"C:\a\b")]
    [InlineData(@"C:\a", @"C:/a/b")]
    [InlineData(@"C:\a", @"C:\a")]
    [InlineData(@"C:\", @"C:\a")]
    public void IsWithin_AcceptsInside(string root, string candidate) => Assert.True(PathSafety.IsWithin(root, candidate));

    [Theory]
    [InlineData(@"C:\a", @"C:\b")]
    [InlineData(@"C:\a", @"D:\a\b")]
    [InlineData(@"C:\a\b", @"C:\a")]
    [InlineData(@"C:\a", @"C:\a\..\..\x")]
    [InlineData(@"C:\a", @"\\server\share\a\b")]
    [InlineData(@"C:\a", @"\\.\C:\a\b")]
    [InlineData(@"C:\a", "")]
    [InlineData(@"C:\a", "C:\\a\\b\0")]
    public void IsWithin_RejectsOutsideAndInvalid(string root, string candidate) => Assert.False(PathSafety.IsWithin(root, candidate));

    [Theory]
    [InlineData(@"C:\a", @"\\?\C:\a\..\b")]
    [InlineData(@"C:\a", @"\\?\C:\a\..\a\b")] // would land inside, but is refused rather than resolved
    [InlineData(@"C:\a", @"\\?\C:\a\.\b")]
    [InlineData(@"C:\a", @"\\?\C:\a\\b")]
    [InlineData(@"C:\a", @"\\?\C:\a/b")]
    [InlineData(@"C:\a", @"\\?\UNC\srv\share\..\x")]
    [InlineData(@"\\?\C:\a\..\b", @"C:\b")]
    [InlineData(@"C:\a", @"C:a\b")] // drive-relative
    [InlineData(@"C:\a", @"\a\b")] // rooted without a drive
    [InlineData(@"C:\a", @"a\b")] // relative
    [InlineData(@"C:\a", @".\b")]
    [InlineData(@"C:\a", @"..\b")]
    [InlineData(@"C:a", @"C:\a\b")]
    [InlineData(@"\a", @"C:\a\b")]
    [InlineData(@"a", @"a\b")]
    [InlineData(@"C:\a", @"\\?\C:")]
    [InlineData(@"C:\a", @"\\?\")]
    [InlineData(@"C:\a", @"\\?\UNC\srv")]
    public void IsWithin_RejectsAmbiguousOrRelativeInput(string root, string candidate) => Assert.False(PathSafety.IsWithin(root, candidate));

    [Theory]
    [InlineData(@"\\srv\share\a", @"\\?\UNC\srv\share\a\b")]
    [InlineData(@"\\?\UNC\srv\share\a", @"\\SRV\Share\A\b")]
    [InlineData(@"\\srv\share", @"\\srv\share\x")]
    [InlineData(@"\\srv\share\", @"\\srv\share\x")]
    [InlineData(@"\\?\UNC\srv\share\", @"\\srv\share\x\")]
    [InlineData(@"\\?\C:\a\", @"\\?\C:\a\b\")]
    [InlineData(@"C:\a\", @"C:\a\")]
    [InlineData(@"C:\", @"\\?\C:\")]
    [InlineData(@"\\?\C:\", @"C:\x\y\")]
    public void IsWithin_UncAndTrailingSeparators(string root, string candidate) => Assert.True(PathSafety.IsWithin(root, candidate));

    [Theory]
    [InlineData(@"\\srv\share\a", @"\\srv\share\ab")]
    [InlineData(@"\\srv\share", @"\\srv\share2\x")]
    [InlineData(@"\\srv\share\a", @"\\srv\other\a\b")]
    [InlineData(@"\\?\C:\a\", @"\\?\C:\ab\")]
    public void IsWithin_UncAndTrailingSeparators_Outside(string root, string candidate) => Assert.False(PathSafety.IsWithin(root, candidate));

    [Fact]
    public void IsWithin_KeepsTrailingDotNamesDistinct()
    {
        Assert.True(PathSafety.IsWithin(@"C:\a", @"C:\a\foo."));
        Assert.False(PathSafety.IsWithin(@"C:\a.", @"C:\a\b"));
    }

    [Fact]
    public void Normalize_GivesCanonicalLongForm()
    {
        Assert.Equal(@"\\?\C:\b", PathSafety.Normalize(@"C:\a\..\b"));
        Assert.Equal(PathSafety.Normalize(@"C:\a\b"), PathSafety.Normalize(@"\\?\C:\a\b"));
    }
}
