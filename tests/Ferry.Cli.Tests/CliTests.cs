using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EveryApp.Core.Jobs;

namespace Ferry.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "cli-test-data", Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly string _source;
    private readonly string _destination;

    public CliTests()
    {
        _app = Path.Combine(_root, "app");
        _source = Path.Combine(_root, "source");
        _destination = Path.Combine(_root, "destination");
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_source);
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            // Each child gets a portable install, so tests never read or write the user's real job history.
            File.Copy(path, Path.Combine(_app, Path.GetFileName(path)));
        }

        File.WriteAllText(Path.Combine(_app, "portable.flag"), "");
    }

    [Fact]
    public async Task Cli_Copy_Succeeds_Exit0()
    {
        var file = SourceFile("hello.txt", "Hello Ferry");
        var result = await RunAsync("copy", file, _destination);
        Assert.Equal(0, result.Code);
        Assert.Equal("Hello Ferry", File.ReadAllText(Path.Combine(_destination, "hello.txt")));
        Assert.Contains("Copied 1", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_JsonOutput_IsValidJsonLines()
    {
        var result = await RunAsync("copy", SourceFile("a.txt", "a"), _destination, "--if-exists", "skip", "--json");
        Assert.Equal(0, result.Code);
        var events = Events(result.Output);
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "progress");
        Assert.Equal("result", events[^1].GetProperty("type").GetString());
        Assert.True(events[^1].GetProperty("allVerified").GetBoolean());
        Assert.Single(events, e => e.GetProperty("type").GetString() == "result");
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Cli_PreflightBlocked_Exit2_PlainMessage()
    {
        File.WriteAllText(@"\\?\" + Path.Combine(_source, "NUL.txt"), "reserved name");
        var result = await RunAsync("copy", _source, _destination);
        Assert.Equal(2, result.Code);
        Assert.Contains("Windows keeps for itself", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_destination));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("copy", "one")]
    [InlineData("copy", "one", "two", "--unknown")]
    [InlineData("copy", "one", "two", "--if-exists", "wrong")]
    [InlineData("copy", "one", "two", "--limit", "-1MB/s")]
    [InlineData("copy", "one", "two", "--limit", "99999999999999999999GB/s")]
    [InlineData("copy", "one", "two", "--json")]
    [InlineData("copy", "one", "two", "--if-unreadable", "ask")]
    [InlineData("resume", "../bad")]
    [InlineData("verify", "one")]
    [InlineData("jobs", "extra")]
    public async Task Cli_InvalidArgs_Exit4(params string[] args)
    {
        var result = await RunAsync(args);
        Assert.Equal(4, result.Code);
        Assert.False(Directory.Exists(_destination));
    }

    [Fact]
    public async Task Cli_Rescue_HealthyFile_CopiesAndSucceeds()
    {
        var file = SourceFile("healthy.txt", "Nothing to rescue");
        var result = await RunAsync("copy", file, _destination, "--rescue");
        Assert.Equal(0, result.Code);
        Assert.Equal("Nothing to rescue", File.ReadAllText(Path.Combine(_destination, "healthy.txt")));
        Assert.False(File.Exists(Path.Combine(_destination, "healthy.txt.ferry-lost.txt")));
    }

    [Fact]
    public async Task Cli_InvalidArgs_JsonStillHasOneResult()
    {
        var result = await RunAsync("copy", "--unknown", "--json");
        Assert.Equal(4, result.Code);
        Assert.Equal(4, Events(result.Output)[^1].GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Cli_Verify_ReportsDifferences()
    {
        SourceFile("same.txt", "same");
        SourceFile("different.txt", "left");
        SourceFile("same-size.txt", "abcd");
        SourceFile("missing.txt", "left only");
        Directory.CreateDirectory(_destination);
        File.WriteAllText(Path.Combine(_destination, "same.txt"), "same");
        File.WriteAllText(Path.Combine(_destination, "different.txt"), "right");
        File.WriteAllText(Path.Combine(_destination, "same-size.txt"), "abce"); // same size, different bytes: caught by the hash
        File.WriteAllText(Path.Combine(_destination, "extra.txt"), "right only");
        Directory.CreateDirectory(Path.Combine(_destination, "empty"));
        var result = await RunAsync("verify", _source, _destination, "--json");
        Assert.Equal(1, result.Code);
        Assert.Equal(5, Events(result.Output).Count(e => e.GetProperty("type").GetString() == "problem"));
        Assert.Contains("File contents differ", result.Output, StringComparison.Ordinal);
        Assert.Contains("Missing from", result.Output, StringComparison.Ordinal);
        Assert.Contains("Only in", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, (await RunAsync("verify", _source, _source)).Code);
        Assert.Equal(1, (await RunAsync("verify", Path.Combine(_root, "missing"), _source)).Code);
    }

    [Fact]
    public async Task Cli_DryRun_MultipleSourcesAndExcludes_DoNotWrite()
    {
        SourceFile("keep.txt", "keep");
        SourceFile("skip.tmp", "exclude");
        SourceFile("skip.bak", "exclude");
        var second = Path.Combine(_root, "second.txt");
        File.WriteAllText(second, "second");
        var result = await RunAsync("copy", _source, second, _destination, "--exclude", "*.tmp", "--exclude", "*.bak", "--dry-run");
        Assert.Equal(0, result.Code);
        Assert.Contains("keep.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains("second.txt", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.tmp", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("skip.bak", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_destination));
        Assert.Empty(Directory.EnumerateFiles(Jobs, "*.journal"));
    }

    [Fact]
    public async Task Cli_Move_PreviewsAndRequiresConfirmation()
    {
        var source = SourceFile("move.txt", "original");
        var refused = await RunAsync("copy", source, _destination, "--move");
        Assert.Equal(2, refused.Code);
        Assert.Contains("Move:", refused.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_destination));
        var accepted = await RunAsync("copy", source, _destination, "--move", "--yes");
        Assert.Equal(0, accepted.Code);
        Assert.False(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_destination, "move.txt")));
    }

    [Theory]
    [InlineData("skip", "old", false)]
    [InlineData("overwrite", "new", false)]
    [InlineData("newer", "new", false)]
    [InlineData("rename", "old", true)]
    [InlineData("keep-both", "old", true)]
    public async Task Cli_ConflictRules(string rule, string expected, bool renamed)
    {
        var source = SourceFile("file.txt", "new");
        Directory.CreateDirectory(_destination);
        var target = Path.Combine(_destination, "file.txt");
        File.WriteAllText(target, "old");
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
        var result = await RunAsync("copy", source, _destination, "--if-exists", rule, "--yes", "--no-verify", "--json");
        Assert.Equal(0, result.Code);
        Assert.Equal(expected, File.ReadAllText(target));
        Assert.Equal(renamed, File.Exists(Path.Combine(_destination, "file (2).txt")));
        Assert.False(Events(result.Output)[^1].GetProperty("allVerified").GetBoolean());
    }

    [Fact]
    public async Task Cli_Ask_UsesRedirectedInputAndApplyToAll()
    {
        SourceFile("a.txt", "new a");
        SourceFile("b.txt", "new b");
        Directory.CreateDirectory(Path.Combine(_destination, "source"));
        File.WriteAllText(Path.Combine(_destination, "source", "a.txt"), "old a");
        File.WriteAllText(Path.Combine(_destination, "source", "b.txt"), "old b");
        var result = await RunWithInputAsync("r\ny\n", "copy", _source, _destination);
        Assert.Equal(0, result.Code);
        Assert.Equal("new a", File.ReadAllText(Path.Combine(_destination, "source", "a.txt")));
        Assert.Equal("new b", File.ReadAllText(Path.Combine(_destination, "source", "b.txt")));
        Assert.Contains("Apply to all?", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_Jobs_ReadsHistoryWithoutChangingCorruptTail()
    {
        await RunAsync("copy", SourceFile("a.txt", "a"), _destination);
        var journal = Assert.Single(Directory.GetFiles(Jobs, "*.journal"));
        File.AppendAllText(journal, "{partial");
        var before = File.ReadAllBytes(journal);
        var result = await RunAsync("jobs", "--json");
        Assert.Equal(0, result.Code);
        Assert.Contains("copied 1", result.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(journal));
        Assert.Contains(Events(result.Output), e => e.TryGetProperty("date", out _));
    }

    [Fact]
    public async Task Cli_ResponseFilesAreNotExpanded_AndDashNamesAreLiteral()
    {
        File.WriteAllText(Path.Combine(_root, "response.txt"), "copy malicious destination");
        Assert.Equal(4, (await RunAsync("@response.txt")).Code);
        File.WriteAllText(Path.Combine(_root, "--json"), "literal");
        var result = await RunAsync("copy", "--", "--json", _destination);
        Assert.Equal(0, result.Code);
        Assert.Equal("literal", File.ReadAllText(Path.Combine(_destination, "--json")));
        Assert.DoesNotContain("{\"type\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_CorruptJob_IsReportedWithoutAStackTrace()
    {
        Directory.CreateDirectory(Jobs);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(Jobs, id.ToString("N") + ".journal"), "not a journal\n");
        var resume = await RunAsync("resume", id.ToString("D"), "--yes", "--json");
        Assert.Equal(2, resume.Code);
        Assert.DoesNotContain("Exception", resume.Output, StringComparison.Ordinal);
        Assert.Equal(1, (await RunAsync("jobs", "--json")).Code);
    }

    [Fact]
    public async Task Cli_Help_WorksWithoutRequiredArguments()
    {
        var help = await RunAsync("copy", "--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("--if-exists", help.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ResumeAfterKill_CompletesWithoutDuplicates()
    {
        for (var i = 0; i < 2000; i++)
        {
            SourceFile($"{i:0000}.txt", new string('x', 4096) + i.ToString(CultureInfo.InvariantCulture));
        }

        using var child = Start("copy", _source, _destination, "--if-exists", "keep-both", "--json");
        var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Guid? jobId = null;
        try
        {
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                jobId ??= item.GetProperty("jobId").GetGuid();
                if (item.TryGetProperty("filesDone", out var count) && count.GetInt32() >= 1000)
                {
                    Assert.True(count.GetInt32() < 2000, "Copy finished before the kill checkpoint.");
                    child.Kill(true);
                    break;
                }
            }

            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        Assert.NotNull(jobId);
        Assert.NotEqual(0, child.ExitCode);
        Assert.Empty(await error);
        var refusal = await RunAsync("resume", jobId.Value.ToString("D"), "--json");
        Assert.Equal(2, refusal.Code);
        var result = await RunAsync("resume", jobId.Value.ToString("D"), "--json", "--yes");
        Assert.Equal(0, result.Code);
        var copied = Path.Combine(_destination, "source");
        Assert.Equal(2000, Directory.GetFiles(copied).Length);
        foreach (var file in Directory.GetFiles(_source))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(copied, Path.GetFileName(file))));
        }

        var records = File.ReadLines(Path.Combine(Jobs, jobId.Value.ToString("N") + ".journal"))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var done = records.Select(d => d.RootElement.GetProperty("r"))
                .Where(r => r.GetProperty("Kind").GetInt32() == (int)JournalRecordKind.FileDone)
                .Select(r => r.GetProperty("RelativePath").GetString()).ToArray();
            Assert.Equal(2000, done.Length);
            Assert.Equal(2000, done.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            foreach (var record in records)
            {
                record.Dispose();
            }
        }

        Assert.Empty(Directory.GetFiles(_destination, "*.ferrypart", SearchOption.AllDirectories));
        Assert.Equal(0, (await RunAsync("resume", jobId.Value.ToString("D"), "--yes", "--json")).Code);
    }

    private string Jobs => Path.Combine(_app, "data", "Jobs");

    /// <summary>Writes a saved job that never ran, as anyone who can write the jobs folder could (every option is written, as Ferry does).</summary>
    private string SaveJob(string mode, Dictionary<string, object> changes)
    {
        var options = new Dictionary<string, object>
        {
            ["Verify"] = true,
            ["KeepDatesAndAttributes"] = true,
            ["CopyPermissions"] = false,
            ["IfExists"] = "Skip",
            ["IfUnreadable"] = "RetryThenSkipAndList",
            ["RescueMode"] = false,
            ["MaxLostSectors"] = 4096,
            ["FollowReparsePoints"] = false,
            ["Exclude"] = Array.Empty<string>(),
            ["DryRun"] = false,
            ["HashAlgorithm"] = "XxHash3",
            ["BufferSizeBytes"] = 0,
            ["CopyAlternateStreams"] = false,
            ["ExportChecksums"] = false,
        };
        foreach (var (key, value) in changes)
        {
            options[key] = value;
        }

        var id = Guid.NewGuid();
        var spec = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = 1,
            ["Sources"] = new[] { _source },
            ["Destination"] = _destination,
            ["Mode"] = mode,
            ["Options"] = options,
        });
        JobJournal.Create(Jobs, id, spec).Dispose();
        return id.ToString("D");
    }

    [Fact]
    public async Task Cli_Resume_RiskySavedOptions_ArePreviewedAndNeedConfirmation()
    {
        SourceFile("a.txt", "new");
        Directory.CreateDirectory(Path.Combine(_destination, "source"));
        File.WriteAllText(Path.Combine(_destination, "source", "a.txt"), "old");
        var id = SaveJob("Copy", new() { ["IfExists"] = "Overwrite", ["FollowReparsePoints"] = true, ["Verify"] = false, ["CopyPermissions"] = true });

        var refused = await RunAsync("resume", id);

        Assert.Equal(2, refused.Code);
        Assert.Contains("a.txt", refused.Output, StringComparison.Ordinal); // the dry-run preview lists what would change
        Assert.Contains("Replaces files that are already in the destination", refused.Output, StringComparison.Ordinal);
        Assert.Contains("Follows links", refused.Output, StringComparison.Ordinal);
        Assert.Contains("Doesn't check the copies", refused.Output, StringComparison.Ordinal);
        Assert.Contains("Copies the files' permissions", refused.Output, StringComparison.Ordinal);
        Assert.Contains("other people may be able to change", refused.Output, StringComparison.Ordinal); // portable install
        Assert.Equal("old", File.ReadAllText(Path.Combine(_destination, "source", "a.txt")));

        var accepted = await RunAsync("resume", id, "--yes");

        Assert.Equal(0, accepted.Code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_destination, "source", "a.txt")));
    }

    [Fact]
    public async Task Cli_Resume_SavedMove_IsPreviewedBeforeAnythingMoves()
    {
        var source = SourceFile("m.txt", "original");
        var id = SaveJob("Move", new());

        var refused = await RunAsync("resume", id, "--json");

        Assert.Equal(2, refused.Code);
        Assert.Contains(Events(refused.Output), e => e.TryGetProperty("phase", out var p) && p.GetString() == "Preview"
            && e.TryGetProperty("source", out var s) && s.GetString()!.EndsWith("m.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("deleted", refused.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData("a‮b")] // right-to-left override
    [InlineData("a⁦b")] // isolate
    [InlineData("a​b")] // zero-width space
    [InlineData("a﻿b")]
    [InlineData("a‎b")]
    public void Cli_Safe_ReplacesInvisibleAndDirectionCharacters(string text) => Assert.Equal("a b", CliOutput.Safe(text));

    [Fact]
    public async Task Cli_CtrlC_Exit3_NoPartFilesLeft()
    {
        var source = Path.Combine(_source, "large.bin");
        using (var file = File.Create(source))
        {
            file.SetLength(64L * 1024 * 1024);
        }

        var start = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(AppContext.BaseDirectory, "ConsoleSignal.ps1"), "-Executable", Path.Combine(_app, "ferry.exe"),
            "-Source", source, "-Destination", _destination })
        {
            start.ArgumentList.Add(argument);
        }

        using var helper = Process.Start(start)!;
        var error = helper.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = helper.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await helper.WaitForExitAsync(timeout.Token);
            Assert.True(helper.ExitCode == 3, $"Exit {helper.ExitCode}: {await error} {await output}");
            Assert.Empty(Directory.GetFiles(_destination, "*.ferrypart", SearchOption.AllDirectories));
            Assert.False(File.Exists(Path.Combine(_destination, "large.bin")));
            Assert.True(File.Exists(source));
            Assert.Single(Directory.GetFiles(Jobs, "*.journal"));
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill(true);
                await helper.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private string SourceFile(string name, string content)
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(_app, "ferry.exe"))
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    private Task<RunResult> RunAsync(params string[] args) => RunWithInputAsync("", args);

    private async Task<RunResult> RunWithInputAsync(string input, params string[] args)
    {
        using var process = Start(args);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            return new RunResult(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static JsonElement[] Events(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }).ToArray();

    public void Dispose() => Directory.Delete(_root, true);

    private sealed record RunResult(int Code, string Output, string Error);
}
