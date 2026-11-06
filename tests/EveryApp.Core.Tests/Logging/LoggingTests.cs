using EveryApp.Core.Logging;

namespace EveryApp.Core.Tests.Logging;

public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "everyapp-tests-" + Guid.NewGuid().ToString("N"));

    public LoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Scrubber_RedactsUserProfile()
    {
        Assert.Equal(@"%USERPROFILE%\x", SecretScrubber.Scrub(@"C:\Users\Alice\x", @"C:\Users\Alice"));
        Assert.Equal(@"%USERPROFILE%\x", SecretScrubber.Scrub(@"c:\users\ALICE\x", @"C:\Users\Alice"));
        Assert.Equal(@"%USERPROFILE%\x", SecretScrubber.Scrub(@"\\?\C:\Users\Alice\x", @"C:\Users\Alice"));
        Assert.Equal(@"C:\Users\Alicia\x", SecretScrubber.Scrub(@"C:\Users\Alicia\x", @"C:\Users\Ali"));
    }

    [Theory]
    [InlineData("token=abc123 done", "token=*** done")]
    [InlineData("Password=hunter2&x=1", "Password=***&x=1")]
    [InlineData("pwd=\"two words\" end", "pwd=*** end")]
    [InlineData("SECRET=s3", "SECRET=***")]
    [InlineData("apikey=k1 api_key=k2", "apikey=*** api_key=***")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
    [InlineData("authorization: basic dXNlcjpwYXNz", "authorization: basic ***")]
    public void Scrubber_RedactsTokens(string input, string expected)
    {
        Assert.Equal(expected, SecretScrubber.Scrub(input));
    }

    [Fact]
    public void Scrubber_RedactsPrivateKeyBlocks()
    {
        const string key = "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\ndef\n-----END RSA PRIVATE KEY-----";
        Assert.Equal("before *** after", SecretScrubber.Scrub("before " + key + " after"));
        Assert.Equal("x ***", SecretScrubber.Scrub("x -----BEGIN PRIVATE KEY-----\nMIItruncated"));
    }

    [Fact]
    public void Scrubber_LeavesPlainTextAlone()
    {
        Assert.Equal("Copied 12 files", SecretScrubber.Scrub("Copied 12 files"));
        Assert.Equal("", SecretScrubber.Scrub(""));
    }

    [Fact]
    public void LogSetup_WritesScrubbedMessageAndException()
    {
        var logger = LogSetup.Create("testapp", _dir);
        logger.Information("login with password={Pw} for {Path}", "hunter2", @"C:\x");
        logger.Error(new InvalidOperationException("bad token=abc999"), "failed");
        logger.Debug("not written at Information level");
        LogSetup.Close(logger);

        var file = Assert.Single(Directory.GetFiles(_dir, "testapp-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("password=***", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abc999", text);
        Assert.Contains("[INF]", text);
        Assert.Contains("[ERR]", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.DoesNotContain("not written", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    public void LogSetup_RejectsUnsafeAppName(string name)
    {
        Assert.Throws<ArgumentException>(() => LogSetup.Create(name, _dir));
    }
}
