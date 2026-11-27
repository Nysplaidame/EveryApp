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
    /// <see cref="JournalRecord.Detail"/> the part file's path relative to the destination root (ending in <c>.ferrypart</c>), and
    /// <see cref="JournalRecord.TargetPath"/> the real name the finished file takes, relative to the destination root (same folder as the part).
    /// After a crash, resume deletes exactly these part files (never by pattern) for files not yet done.
    /// </summary>
    FilePartStarted,

    /// <summary>
    /// Written right AFTER a part file is created: <see cref="JournalRecord.RelativePath"/> is the file the job is working on,
    /// <see cref="JournalRecord.Detail"/> the part file's path relative to the destination root, and <see cref="JournalRecord.Hash"/> which
    /// object on disk the part is (<see cref="JobJournal.FormatIdentity"/>). A rename keeps that identity, so a resume can tell this job's own
    /// file from another file with the same name and size.
    /// </summary>
    FilePartCreated,
}

/// <summary>A part file named by a <see cref="JournalRecordKind.FilePartStarted"/> record. Both paths are safe relative paths.</summary>
public sealed record JournalPart(string RelativePath, string PartRelativePath, string TargetRelativePath);

/// <summary>Which object on disk a part file was, from a <see cref="JournalRecordKind.FilePartCreated"/> record.</summary>
public sealed record JournalPartIdentity(string RelativePath, string PartRelativePath, ulong VolumeSerial, UInt128 FileId);

/// <summary>Read-only history of a journal's valid records. The options still need the engine's validation.</summary>
public sealed record JournalSummary(string OptionsJson, DateTime StartedUtc, bool IsFinished, string? Result);

/// <summary>
/// One journal entry. For <see cref="JournalRecordKind.JobStarted"/>, <see cref="Detail"/> holds the job options as a JSON string.
/// <see cref="RelativePath"/> is the job's key for the item and must never be rooted or contain <c>..</c>.
/// <see cref="TargetPath"/> is used only by <see cref="JournalRecordKind.FilePartStarted"/> (null for every other kind).
/// </summary>
public sealed record JournalRecord(
    JournalRecordKind Kind,
    string? RelativePath,
    long Length,
    DateTime SourceLastWriteUtc,
    string? Hash,
    string? Detail,
    string? TargetPath = null);

/// <summary>Source-generated JSON metadata for the journal: no reflection, no polymorphism.</summary>
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
