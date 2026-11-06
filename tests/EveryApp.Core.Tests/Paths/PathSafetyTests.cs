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
