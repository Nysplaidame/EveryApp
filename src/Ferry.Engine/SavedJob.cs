using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>A validated saved job, read without modifying its journal. Reading it does not authorize resuming it.</summary>
public sealed record SavedJob(IReadOnlyList<string> Sources, string Destination, CopyMode Mode, CopyOptions Options,
    DateTime StartedUtc, bool IsFinished, string? Result)
{
    public static SavedJob Read(string jobsDirectory, Guid jobId, CancellationToken ct = default)
    {
        var summary = JobJournal.ReadSummary(jobsDirectory, jobId, ct);
        var spec = JobSpec.Parse(summary.OptionsJson);
        return new SavedJob(spec.Sources, spec.Destination, spec.Mode, spec.Options, summary.StartedUtc, summary.IsFinished, summary.Result);
    }
}
