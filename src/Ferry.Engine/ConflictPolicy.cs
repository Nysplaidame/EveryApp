using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Turns the job's conflict rule (asking the resolver when it is Ask) into what to do with one existing destination.</summary>
internal static class ConflictPolicy
{
    /// <summary>Returns Overwrite, Rename, KeepBoth or Skip.</summary>
    public static async ValueTask<ConflictRule> DecideAsync(
        ConflictRule rule, IConflictResolver resolver, FileEntry source, FileEntry existing, CancellationToken ct)
    {
        if (rule == ConflictRule.Ask)
        {
            rule = await resolver.ResolveAsync(source, existing, ct).ConfigureAwait(false);
        }

        return rule switch
        {
            ConflictRule.OverwriteIfNewer => source.LastWriteTimeUtc > existing.LastWriteTimeUtc ? ConflictRule.Overwrite : ConflictRule.Skip,
            ConflictRule.Overwrite or ConflictRule.Rename or ConflictRule.KeepBoth => rule,
            _ => ConflictRule.Skip, // Skip, or Ask answered with Ask
        };
    }
}
