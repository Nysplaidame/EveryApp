using System.Text.Json.Serialization;

namespace EveryApp.Core.Jobs;

/// <summary>What a journal line says happened.</summary>
public enum JournalRecordKind
{
    JobStarted,
    FileDone,
    FileSkipped,
    FileFailed,
    JobFinished,
}

/// <summary>
/// One journal entry. For <see cref="JournalRecordKind.JobStarted"/>, <see cref="Detail"/> holds the job options as a JSON string.
/// <see cref="RelativePath"/> is relative to the job's source root and must never be rooted or contain <c>..</c>.
/// </summary>
public sealed record JournalRecord(
    JournalRecordKind Kind,
    string? RelativePath,
    long Length,
    DateTime SourceLastWriteUtc,
    string? Hash,
    string? Detail);

/// <summary>Source-generated JSON metadata for the journal: no reflection, no polymorphism.</summary>
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
