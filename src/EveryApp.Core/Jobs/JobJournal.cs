using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace EveryApp.Core.Jobs;

/// <summary>
/// Crash-safe record of what a copy job has done, so it can resume after a crash, power cut or reboot.
/// <para>
/// Format: one JSON object per line, <c>{"crc":N,"r":{...}}</c>, where N is the CRC32 of the exact UTF-8 bytes of the <c>r</c> object.
/// The first line is always the <see cref="JournalRecordKind.JobStarted"/> header, which carries the job options.
/// </para>
/// <para>
/// Durability: the file is flushed to disk every 64 records or 2 seconds (checked on each append, no background thread),
/// whichever comes first, always after <see cref="JournalRecordKind.FileFailed"/> and <see cref="JournalRecordKind.JobFinished"/>,
/// and on dispose. After a crash up to 63 recent <see cref="JournalRecordKind.FileDone"/> records can be missing. That is safe:
/// those files are simply copied again on resume (the copier writes to temporary names, so a repeat never corrupts anything).
/// </para>
/// <para>
/// A journal file is untrusted input when reopened. Reading stops at the first line that is too long (over 64 KB), not valid,
/// fails its checksum, or names a path that is rooted or contains <c>..</c>; only the lines before it are trusted. On reopen the
/// file is cut back to the end of the last good line so that new records never follow garbage.
/// </para>
/// </summary>
public sealed class JobJournal : IDisposable
{
    internal const int MaxLineBytes = 64 * 1024;
    private const int FlushEveryRecords = 64;
    private const int FlushEveryMilliseconds = 2000;
    private static readonly byte[] CrcPrefix = "{\"crc\":"u8.ToArray();
    private static readonly byte[] RecordPrefix = ",\"r\":"u8.ToArray();

    private readonly object _lock = new();
    private readonly FileStream _stream;
    private readonly HashSet<string> _completed;
    private int _unflushed;
    private long _lastFlushTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    private JobJournal(FileStream stream, string optionsJson, HashSet<string> completed, bool isFinished)
    {
        _stream = stream;
        OptionsJson = optionsJson;
        _completed = completed;
        IsFinished = isFinished;
    }

    /// <summary>The options the job was started with.</summary>
    public string OptionsJson { get; }

    /// <summary>True once a <see cref="JournalRecordKind.JobFinished"/> record has been read or appended.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>
    /// A snapshot (a copy) of the relative paths of every <see cref="JournalRecordKind.FileDone"/> record, compared ignoring case
    /// because Windows paths are. Intended to be read once at resume; do not call it per file.
    /// </summary>
    public IReadOnlySet<string> CompletedPaths
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<string>(_completed, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Starts a new journal. Throws <see cref="IOException"/> if one already exists for this job.</summary>
    public static JobJournal Create(string jobsDir, Guid jobId, string optionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);
        ArgumentNullException.ThrowIfNull(optionsJson);

        Directory.CreateDirectory(jobsDir);
        var stream = new FileStream(JournalPath(jobsDir, jobId), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024);
        try
        {
            var journal = new JobJournal(stream, optionsJson, new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
            var header = new JournalRecord(JournalRecordKind.JobStarted, null, 0, DateTime.UtcNow, null, optionsJson);
            journal.Append(header, forceFlush: true);
            return journal;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reopens an existing journal to resume. Throws <see cref="FileNotFoundException"/> if there is none and
    /// <see cref="InvalidDataException"/> if even the header line cannot be trusted.
    /// </summary>
    public static JobJournal Open(string jobsDir, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);

        var path = JournalPath(jobsDir, jobId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The job journal for this job was not found, so it cannot be resumed.", path);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 16 * 1024);
        try
        {
            var load = Load(stream);
            if (load.Options is null)
            {
                throw new InvalidDataException("The job journal is damaged: its first line cannot be read.");
            }

            // Cut off anything after the last good line, so new records are not stranded behind garbage.
            stream.SetLength(load.GoodLength);
            stream.Seek(0, SeekOrigin.End);
            return new JobJournal(stream, load.Options, load.Completed, load.Finished);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Adds a record. Safe to call from several threads. Flushing follows the policy in the class summary.</summary>
    public void Append(JournalRecord record) => Append(record, forceFlush: false);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _stream.Flush(true);
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }

    private void Append(JournalRecord record, bool forceFlush)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(record.Kind))
        {
            throw new ArgumentException("Unknown record kind.", nameof(record));
        }

        if (record.RelativePath is not null && !IsSafeRelativePath(record.RelativePath))
        {
            throw new ArgumentException("The path must be relative and must not contain '..'.", nameof(record));
        }

        // Serialise outside the lock; only the write and flush decision are serialised.
        var line = BuildLine(record);
        if (line.Length > MaxLineBytes)
        {
            throw new ArgumentException("The record is too large for the journal.", nameof(record));
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _stream.Write(line);
            _unflushed++;

            if (record.Kind == JournalRecordKind.FileDone)
            {
                _completed.Add(record.RelativePath!);
            }
            else if (record.Kind == JournalRecordKind.JobFinished)
            {
                IsFinished = true;
            }

            var mustFlush = forceFlush
                || record.Kind is JournalRecordKind.JobFinished or JournalRecordKind.FileFailed
                || _unflushed >= FlushEveryRecords
                || Stopwatch.GetElapsedTime(_lastFlushTimestamp).TotalMilliseconds >= FlushEveryMilliseconds;

            if (mustFlush)
            {
                _stream.Flush(true);
                _unflushed = 0;
                _lastFlushTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private static string JournalPath(string jobsDir, Guid jobId) =>
        Path.Combine(jobsDir, jobId.ToString("N") + ".journal");

    private static byte[] BuildLine(JournalRecord record)
    {
        var r = JsonSerializer.SerializeToUtf8Bytes(record, JournalJsonContext.Default.JournalRecord);
        var crc = Encoding.ASCII.GetBytes(Crc32.HashToUInt32(r).ToString(CultureInfo.InvariantCulture));

        var line = new byte[CrcPrefix.Length + crc.Length + RecordPrefix.Length + r.Length + 2];
        var pos = 0;
        CrcPrefix.CopyTo(line, pos);
        pos += CrcPrefix.Length;
        crc.CopyTo(line, pos);
        pos += crc.Length;
        RecordPrefix.CopyTo(line, pos);
        pos += RecordPrefix.Length;
        r.CopyTo(line, pos);
        pos += r.Length;
        line[pos++] = (byte)'}';
        line[pos] = (byte)'\n';
        return line;
    }

    private readonly record struct LoadResult(string? Options, HashSet<string> Completed, bool Finished, long GoodLength);

    /// <summary>Reads lines from the start and stops at the first one that cannot be trusted.</summary>
    private static LoadResult Load(FileStream stream)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? options = null;
        var finished = false;
        long goodLength = 0;

        stream.Seek(0, SeekOrigin.Begin);
        var chunk = new byte[16 * 1024];
        var line = new byte[MaxLineBytes];
        var lineLength = 0;
        long consumed = 0;
        var stop = false;

        while (!stop)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break; // end of file; anything left in `line` has no newline, so it is a truncated last line
            }

            for (var i = 0; i < read && !stop; i++)
            {
                consumed++;
                var b = chunk[i];
                if (b != (byte)'\n')
                {
                    if (lineLength == MaxLineBytes)
                    {
                        stop = true; // too long: corruption
                    }
                    else
                    {
                        line[lineLength++] = b;
                    }

                    continue;
                }

                if (!TryParseLine(line.AsSpan(0, lineLength), out var record)
                    || !Accept(record, options is null, completed, ref options, ref finished))
                {
                    stop = true;
                    continue;
                }

                goodLength = consumed;
                lineLength = 0;
            }
        }

        return new LoadResult(options, completed, finished, goodLength);
    }

    private static bool Accept(JournalRecord record, bool isFirst, HashSet<string> completed, ref string? options, ref bool finished)
    {
        if (isFirst)
        {
            if (record.Kind != JournalRecordKind.JobStarted || record.Detail is null)
            {
                return false;
            }

            options = record.Detail;
            return true;
        }

        switch (record.Kind)
        {
            case JournalRecordKind.JobStarted:
                return false; // only one header
            case JournalRecordKind.FileDone:
                if (record.RelativePath is null)
                {
                    return false;
                }

                completed.Add(record.RelativePath);
                return true;
            case JournalRecordKind.JobFinished:
                finished = true;
                return true;
            default:
                return true;
        }
    }

    private static bool TryParseLine(ReadOnlySpan<byte> line, out JournalRecord record)
    {
        record = null!;

        // Strict shape: {"crc":<digits>,"r":<json object>}
        if (!line.StartsWith(CrcPrefix) || line.Length < CrcPrefix.Length + 1 + RecordPrefix.Length + 2 || line[^1] != (byte)'}')
        {
            return false;
        }

        var rest = line[CrcPrefix.Length..];
        var comma = rest.IndexOf((byte)',');
        if (comma < 1 || comma > 10)
        {
            return false;
        }

        if (!uint.TryParse(rest[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
        {
            return false;
        }

        rest = rest[comma..];
        if (!rest.StartsWith(RecordPrefix))
        {
            return false;
        }

        var r = rest[RecordPrefix.Length..^1];
        if (Crc32.HashToUInt32(r) != expected)
        {
            return false;
        }

        JournalRecord? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(r, JournalJsonContext.Default.JournalRecord);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null || !Enum.IsDefined(parsed.Kind))
        {
            return false;
        }

        if (parsed.RelativePath is not null && !IsSafeRelativePath(parsed.RelativePath))
        {
            return false;
        }

        record = parsed;
        return true;
    }

    /// <summary>Rejects empty, rooted (drive, UNC, leading slash) and parent-escaping paths, so a tampered journal cannot point outside the destination.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path.Contains('\0') || Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }
}
