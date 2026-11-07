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

    /// <summary>
    /// Written BEFORE a part file is created: <see cref="JournalRecord.RelativePath"/> is the file the job is working on and
    /// <see cref="JournalRecord.Detail"/> the part file's path relative to the destination root (ending in <c>.ferrypart</c>).
    /// After a crash, resume deletes exactly these part files (never by pattern) for files not yet done.
    /// </summary>
    FilePartStarted,
}

/// <summary>A part file named by a <see cref="JournalRecordKind.FilePartStarted"/> record. Both paths are safe relative paths.</summary>
public sealed record JournalPart(string RelativePath, string PartRelativePath);

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
