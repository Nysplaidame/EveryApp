using EveryApp.Core.FileSystem;
using Ferry.Engine;

namespace Ferry.Cli;

internal sealed class ConsolePrompts(CliOutput output, bool yes, bool json, bool portable = false) : IConflictResolver, IResumeConfirmation
{
    /// <summary>Shown before resuming in a portable install, whose saved jobs sit next to the program where other accounts may write.</summary>
    internal const string PortableWarning =
        "This copy of Ferry keeps its saved jobs next to the program, where other people may be able to change them. Check the details above before you continue.";

    internal const string ChangedSincePreview = "The saved job changed after it was shown, so nothing was done. Run the command again to see it.";

    private ConflictRule? _all;

    /// <summary>The saved job as the preview showed it. When set, a resume whose details differ is refused, even with --yes.</summary>
    public SavedJob? Previewed { get; set; }

    public async ValueTask<bool> ConfirmAsync(ResumeRequest request, CancellationToken ct)
    {
        output.Preview(request);
        foreach (var line in request.RiskyOptions)
        {
            output.PreviewNote(line);
        }

        if (portable)
        {
            output.PreviewNote(PortableWarning);
        }

        if (Previewed is { } shown && !SameJob(shown, request))
        {
            output.Problem(ChangedSincePreview);
            return false;
        }

        return yes || (!json && await ConfirmAsync("Continue this job? [y/N]", ct).ConfigureAwait(false));
    }

    private static bool SameJob(SavedJob shown, ResumeRequest request)
    {
        var none = Array.Empty<string>();
        return shown.Mode == request.Mode
            && string.Equals(shown.Destination, request.Destination, StringComparison.Ordinal)
            && shown.Sources.SequenceEqual(request.Sources, StringComparer.Ordinal)
            && shown.Options.Exclude.SequenceEqual(request.Options.Exclude, StringComparer.Ordinal)
            && shown.Options with { Exclude = none } == request.Options with { Exclude = none };
    }

    public static async ValueTask<bool> ConfirmAsync(string message, CancellationToken ct)
    {
        Console.Error.WriteLine(CliOutput.Safe(message));
        return string.Equals(await ReadLineAsync(ct).ConfigureAwait(false), "y", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<ConflictRule> ResolveAsync(FileEntry source, FileEntry existing, CancellationToken ct)
    {
        if (_all is { } rule)
        {
            return rule;
        }

        if (json)
        {
            throw new ArgumentException("Choose an existing-file rule before using --json.");
        }

        Console.Error.WriteLine(CliOutput.Safe($"File already exists: {existing.FullPath}"));
        Console.Error.WriteLine("Replace / Skip / Keep both / Replace if newer [r/s/k/n, default: Skip]");
        var answer = await ReadLineAsync(ct).ConfigureAwait(false);
        rule = answer?.Trim().ToUpperInvariant() switch
        {
            "R" or "REPLACE" => ConflictRule.Overwrite,
            "K" or "KEEP BOTH" => ConflictRule.KeepBoth,
            "N" or "REPLACE IF NEWER" => ConflictRule.OverwriteIfNewer,
            _ => ConflictRule.Skip,
        };
        if (await ConfirmAsync("Apply to all? [y/N]", ct).ConfigureAwait(false))
        {
            _all = rule;
        }

        return rule;
    }

    // Console.In can implement ReadLineAsync synchronously. Wait on a background read so Ctrl+C
    // can still stop a job while a person (or a redirected input pipe) has not answered yet.
    private static Task<string?> ReadLineAsync(CancellationToken ct) => Task.Run(Console.ReadLine, ct).WaitAsync(ct);
}
