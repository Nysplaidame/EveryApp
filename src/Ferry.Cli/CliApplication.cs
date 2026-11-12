using System.CommandLine;
using System.Globalization;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Logging;
using EveryApp.Core.Paths;
using EveryApp.Core.Preflight;
using EveryApp.Core.Scheduling;
using EveryApp.Core.Settings;
using Ferry.Engine;

namespace Ferry.Cli;

internal static class CliApplication
{
    private const string Usage = """
        ferry copy <source>... <destination> [--move] [--no-verify]
          [--if-exists ask|overwrite|newer|skip|rename|keep-both]
          [--if-unreadable retry-skip|ask|stop] [--rescue] [--limit 50MB/s]
          [--exclude <glob>]... [--dry-run] [--json] [--yes]
        ferry resume <job-id> [--json] [--yes]
        ferry verify <folderA> <folderB> [--json]
        ferry jobs [--json]
        Moves and automatic replacements show a preview and require confirmation.
        --yes confirms that preview, including the saved paths when resuming.
        With --json, copy requires an explicit --if-exists rule other than ask.
        Rescue and unreadable-file prompts are not available in this version.
        Use -- before names that start with a dash. Response files are disabled.
        Exit codes: 0 success; 1 problems; 2 blocked; 3 cancelled; 4 invalid arguments; 10 internal error.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var json = args.TakeWhile(a => a != "--").Contains("--json", StringComparer.Ordinal);
        var output = new CliOutput(json);
        using var cancel = new CancellationTokenSource();
        var signals = new CancellationSignals(cancel, () => Environment.Exit(ExitCodes.Cancelled));
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            signals.Signal();
        };
        Console.CancelKeyPress += onCancel;
        Serilog.ILogger? logger = null;
        try
        {
            if (args.Length > 4096 || args.Any(a => a.Length > 32767 || a.Any(char.IsControl)))
            {
                throw new ArgumentException("The arguments are too long or contain unsupported characters.");
            }

            var cli = new CliCommands();
            var knownOptions = cli.Root.Options.Concat(cli.Root.Subcommands.SelectMany(c => c.Options))
                .SelectMany(o => o.Aliases.Append(o.Name)).ToHashSet(StringComparer.Ordinal);
            if (args.TakeWhile(a => a != "--").Any(a => a.StartsWith('-') && !knownOptions.Contains(a.Split('=', 2)[0])))
            {
                throw new ArgumentException("An option is not recognised. Put -- before a file name that starts with a dash.");
            }

            var parsed = cli.Root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null, EnablePosixBundling = false });
            if (parsed.GetValue(cli.Help))
            {
                if (json)
                {
                    return output.Result(ExitCodes.Success, Usage);
                }

                Console.WriteLine(Usage);
                return ExitCodes.Success;
            }

            if (parsed.Errors.Count != 0)
            {
                foreach (var error in parsed.Errors)
                {
                    output.Problem(error.Message);
                }

                return output.Result(ExitCodes.InvalidArguments, "Check the arguments. Use --help for examples.");
            }

            var command = parsed.CommandResult.Command.Name;
            if (command is not ("copy" or "resume" or "verify" or "jobs"))
            {
                throw new ArgumentException("Choose copy, resume, verify or jobs. Use --help for examples.");
            }

            logger = LogSetup.Create("ferry");
            logger.Information("Ferry command started: {Command}", command);
            var yes = parsed.GetValue(cli.Yes);
            var prompts = new ConsolePrompts(output, yes, json);
            var copier = new TreeCopier(new WindowsFileSystem(), new DriveAwareScheduler(new WindowsDeviceResolver()), prompts, AppPaths.Jobs);
            int code;
            switch (command)
            {
                case "copy":
                    code = await CopyAsync(parsed, cli, copier, output, json, yes, cancel.Token).ConfigureAwait(false);
                    break;
                case "resume":
                    code = await ResumeAsync(parsed.GetValue(cli.JobId)!, copier, output, prompts, json, cancel.Token).ConfigureAwait(false);
                    break;
                case "verify":
                    var folders = parsed.GetValue(cli.Folders)!;
                    code = await FolderVerifier.RunAsync(FullPath(folders[0]), FullPath(folders[1]), output, cancel.Token).ConfigureAwait(false);
                    break;
                default:
                    code = History(output, cancel.Token);
                    break;
            }

            logger.Information("Ferry command finished with exit code {ExitCode}", code);
            return code;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return output.Result(ExitCodes.Cancelled, "Cancelled. You can resume the saved job.");
        }
        catch (ArgumentException ex)
        {
            output.Problem(ex.Message);
            return output.Result(ExitCodes.InvalidArguments, "Check the arguments. Use --help for examples.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FileSystemException)
        {
            output.Problem("The files or saved job could not be opened. Check that they exist and are available.");
            return output.Result(ExitCodes.Blocked, "The operation could not start or continue.");
        }
#pragma warning disable CA1031 // Last-resort process boundary: stable exit code, no exception details on stdout.
        catch (Exception ex)
        {
            logger?.Error("Ferry stopped unexpectedly: {ErrorType}", ex.GetType().Name);
            return output.Result(ExitCodes.InternalError, "Ferry stopped because of an unexpected problem. Check the local log.");
        }
#pragma warning restore CA1031
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (logger is not null)
            {
                LogSetup.Close(logger);
            }
        }
    }

    private static async Task<int> CopyAsync(ParseResult parsed, CliCommands cli, TreeCopier copier, CliOutput output,
        bool json, bool yes, CancellationToken ct)
    {
        var paths = parsed.GetValue(cli.Paths)!;
        var sources = paths[..^1].Select(FullPath).ToArray();
        var destination = FullPath(paths[^1]);
        var rule = parsed.GetValue(cli.IfExists) switch
        {
            "ask" => ConflictRule.Ask,
            "overwrite" => ConflictRule.Overwrite,
            "newer" => ConflictRule.OverwriteIfNewer,
            "skip" => ConflictRule.Skip,
            "rename" => ConflictRule.Rename,
            "keep-both" => ConflictRule.KeepBoth,
            _ => throw new ArgumentException("Choose ask, overwrite, newer, skip, rename or keep-both for --if-exists."),
        };
        var failure = parsed.GetValue(cli.IfUnreadable) switch
        {
            "retry-skip" => FailureRule.RetryThenSkipAndList,
            "ask" => FailureRule.Ask,
            "stop" => FailureRule.Stop,
            _ => throw new ArgumentException("Choose retry-skip, ask or stop for --if-unreadable."),
        };
        var options = new CopyOptions
        {
            IfExists = rule,
            IfUnreadable = failure,
            Verify = !parsed.GetValue(cli.NoVerify),
            RescueMode = parsed.GetValue(cli.Rescue),
            DryRun = parsed.GetValue(cli.DryRun),
            Exclude = parsed.GetValue(cli.Exclude) ?? [],
            SpeedLimitBytesPerSecond = ParseLimit(parsed.GetValue(cli.Limit)),
        };
        CheckOptions(options, json);
        var mode = parsed.GetValue(cli.Move) ? CopyMode.Move : CopyMode.Copy;
        output.JobId = Guid.NewGuid();
        var pause = new PauseGate();
        if (!options.DryRun && (mode == CopyMode.Move || rule is ConflictRule.Overwrite or ConflictRule.OverwriteIfNewer))
        {
            var preview = await copier.RunAsync(output.JobId.Value, sources, destination, mode, options with { DryRun = true }, pause, output, ct).ConfigureAwait(false);
            foreach (var item in preview.Plan)
            {
                output.Plan(item);
            }

            if (preview.Stopped)
            {
                return Finish(preview, output);
            }

            if (!yes && (json || !await ConsolePrompts.ConfirmAsync("Continue with these changes? [y/N]", ct).ConfigureAwait(false)))
            {
                return output.Result(ExitCodes.Blocked, "No changes made. Confirm the preview, or use --yes.");
            }
        }

        var result = await copier.RunAsync(output.JobId.Value, sources, destination, mode, options, pause, output, ct).ConfigureAwait(false);
        foreach (var item in result.Plan)
        {
            output.Plan(item);
        }

        return Finish(result, output);
    }

    private static async Task<int> ResumeAsync(string id, TreeCopier copier, CliOutput output, ConsolePrompts prompts, bool json, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var jobId) || jobId == Guid.Empty)
        {
            throw new ArgumentException("Use the job id shown by ferry jobs.");
        }

        output.JobId = jobId;
        var saved = SavedJob.Read(AppPaths.Jobs, jobId, ct);
        CheckOptions(saved.Options, json);
        return Finish(await copier.ResumeAsync(jobId, prompts, new PauseGate(), output, ct).ConfigureAwait(false), output);
    }

    private static void CheckOptions(CopyOptions options, bool json)
    {
        if (json && (options.IfExists == ConflictRule.Ask || options.IfUnreadable == FailureRule.Ask))
        {
            throw new ArgumentException("Ask cannot be used with --json. Choose a rule that does not need a prompt.");
        }

        if (options.RescueMode || options.IfUnreadable == FailureRule.Ask)
        {
            throw new ArgumentException("Rescue and unreadable-file prompts are not available in this version.");
        }
    }

    private static int History(CliOutput output, CancellationToken ct)
    {
        var problems = false;
        foreach (var path in Directory.EnumerateFiles(AppPaths.Jobs, "*.journal"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                continue;
            }

            try
            {
                var saved = SavedJob.Read(AppPaths.Jobs, id, ct);
                output.History(id, saved.StartedUtc, string.Join(", ", saved.Sources), saved.Destination,
                    saved.IsFinished ? saved.Result ?? "Finished" : "Unfinished");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problems = true;
                output.Problem($"Saved job {id:D} could not be read.");
            }
        }

        return output.Result(problems ? ExitCodes.Problems : ExitCodes.Success, "Job history listed.");
    }

    private static int Finish(CopyResult result, CliOutput output)
    {
        foreach (var problem in result.Problems)
        {
            output.Problem(problem.Message, problem.Path);
        }

        foreach (var issue in result.PreflightIssues)
        {
            output.Problem(issue.Message, issue.Path);
        }

        if (result.MoreProblems > 0)
        {
            output.Problem($"{result.MoreProblems} more problems were found.");
        }

        var blocked = result.PreflightIssues.Any(i => i.Code is not (PreflightCode.PathTooLong or PreflightCode.TrailingDotOrSpace or PreflightCode.FreeSpaceUnknown or PreflightCode.ZoneMarkerNotKept))
            || (result.Stopped && result.Copied == 0 && result.Failed == 0);
        var code = blocked ? ExitCodes.Blocked
            : result.Stopped || result.Failed > 0 || result.Problems.Count > 0 || result.PreflightIssues.Count > 0 || result.MoreProblems > 0
                ? ExitCodes.Problems : ExitCodes.Success;
        return output.Result(code, blocked ? "The job could not start." : $"Copied {result.Copied}, skipped {result.Skipped}, failed {result.Failed}.", result);
    }

    private static string FullPath(string path) => PathSafety.Normalize(Path.GetFullPath(path));

    private static long? ParseLimit(string? text)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var (suffix, multiplier) in new[] { ("GB/s", 1_000_000_000L), ("MB/s", 1_000_000L), ("KB/s", 1_000L), ("B/s", 1L) })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(text[..^suffix.Length], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                && amount > 0 && amount <= long.MaxValue / multiplier)
            {
                var bytes = decimal.Truncate(amount * multiplier);
                if (bytes >= 1)
                {
                    return (long)bytes;
                }
            }
        }

        throw new ArgumentException("Use a positive speed such as 50MB/s for --limit.");
    }
}

internal sealed class CliCommands
{
    public RootCommand Root { get; } = new("Ferry copy and verify");
    public Option<bool> Json { get; } = new("--json") { Recursive = true };
    public Option<bool> Help { get; } = new("--help", "-h") { Recursive = true };
    public Option<bool> Yes { get; } = new("--yes");
    public Option<bool> Move { get; } = new("--move");
    public Option<bool> NoVerify { get; } = new("--no-verify");
    public Option<bool> DryRun { get; } = new("--dry-run");
    public Option<bool> Rescue { get; } = new("--rescue");
    public Option<string> IfExists { get; } = new("--if-exists") { DefaultValueFactory = _ => "ask" };
    public Option<string> IfUnreadable { get; } = new("--if-unreadable") { DefaultValueFactory = _ => "retry-skip" };
    public Option<string?> Limit { get; } = new("--limit");
    public Option<string[]> Exclude { get; } = new("--exclude") { AllowMultipleArgumentsPerToken = false, Arity = ArgumentArity.OneOrMore };
    public Argument<string[]> Paths { get; } = new("paths") { Arity = new ArgumentArity(2, 4096) };
    public Argument<string> JobId { get; } = new("job-id");
    public Argument<string[]> Folders { get; } = new("folders") { Arity = new ArgumentArity(2, 2) };

    public CliCommands()
    {
        Root.Options.Clear();
        Root.Directives.Clear();
        Root.Options.Add(Json);
        Root.Options.Add(Help);
        var copy = new Command("copy");
        copy.Arguments.Add(Paths);
        foreach (var option in new Option[] { Yes, Move, NoVerify, DryRun, Rescue, IfExists, IfUnreadable, Limit, Exclude })
        {
            copy.Options.Add(option);
        }

        var resume = new Command("resume");
        resume.Arguments.Add(JobId);
        resume.Options.Add(Yes);
        var verify = new Command("verify");
        verify.Arguments.Add(Folders);
        Root.Subcommands.Add(copy);
        Root.Subcommands.Add(resume);
        Root.Subcommands.Add(verify);
        Root.Subcommands.Add(new Command("jobs"));
    }
}

internal sealed class CancellationSignals(CancellationTokenSource source, Action forceExit)
{
    private readonly Lock _gate = new();
    private long? _lastSignal;

    public void Signal()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_lastSignal is { } last && now - last <= 2000)
            {
                forceExit();
                return;
            }

            _lastSignal = now;
            source.Cancel();
        }
    }
}
