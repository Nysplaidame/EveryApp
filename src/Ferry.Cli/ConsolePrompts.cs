using EveryApp.Core.FileSystem;
using Ferry.Engine;

namespace Ferry.Cli;

internal sealed class ConsolePrompts(CliOutput output, bool yes, bool json) : IConflictResolver, IResumeConfirmation
{
    private ConflictRule? _all;

    public async ValueTask<bool> ConfirmAsync(ResumeRequest request, CancellationToken ct)
    {
        output.Preview(request);
        return yes || (!json && await ConfirmAsync("Continue this job? [y/N]", ct).ConfigureAwait(false));
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
