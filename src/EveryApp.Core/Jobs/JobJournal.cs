using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;

namespace EveryApp.Core.Jobs;

/// <summary>Test seam for the two disk operations that can fail. Production code always uses the defaults.</summary>
internal sealed class JournalIo
{
    public static readonly Action<FileStream, byte[]> DefaultWrite = static (s, line) => s.Write(line);
    public static readonly Action<FileStream> DefaultFlush = static s => s.Flush(true);
    public static readonly Action<FileStream> DefaultHandOver = static s => s.Flush(false);

    public Action<FileStream, byte[]> Write { get; set; } = DefaultWrite;

    public Action<FileStream> Flush { get; set; } = DefaultFlush;

    /// <summary>Hands buffered bytes to the operating system without waiting for the disk (survives a process crash, not a power cut).</summary>
    public Action<FileStream> HandOver { get; set; } = DefaultHandOver;
}

/// <summary>
/// Crash-safe record of what a copy job has done, so it can resume after a crash, power cut or reboot.
/// <para>
/// Format: one JSON object per line (LF only), <c>{"crc":N,"r":{...}}</c>, where N is the CRC32 of the exact UTF-8 bytes of the
/// <c>r</c> object. The first line is always the <see cref="JournalRecordKind.JobStarted"/> header, which carries the job options.
/// </para>
/// <para>
/// A <see cref="JournalRecordKind.FileDone"/> record whose <see cref="JournalRecord.Detail"/> is <see cref="NotOnDiskYet"/> names a file renamed into
/// place without being forced to disk; see <see cref="NotOnDiskPaths"/>.
/// </para>
/// <para>
/// Durability: the file is flushed to disk every 64 records or 2 seconds, whichever comes first, always after
/// <see cref="JournalRecordKind.FileFailed"/> and <see cref="JournalRecordKind.JobFinished"/>, and on dispose.
/// A <see cref="JournalRecordKind.FilePartStarted"/> record is always handed to the operating system at once (not flushed to disk), so a
/// crashed process never leaves an unnamed part file; after a power cut a part file can be left over (harmless, never under a real name). The 2-second rule
/// is checked only when the next record is appended (there is no background timer), so an idle tail of up to 63 records
/// may stay buffered until the next <see cref="Append"/> or <see cref="Dispose"/>. After a crash those FileDone records can be
/// missing. That is safe: the files are simply copied again on resume (the copier writes to temporary names, so a repeat never
/// corrupts anything). Tests can only prove that data reached the operating system; they cannot prove it reached the platter.
/// </para>
/// <para>
/// A journal file is untrusted input when reopened. Reading stops at the first line that is too long (over 64 KB), uses CRLF,
/// is not valid, fails its checksum, or breaks the record rules (see <see cref="Validate"/>); only the lines before it are
/// trusted. A last line without its newline is dropped even if it looks complete, because a crash can cut a line anywhere.
/// On reopen the file is cut back to the end of the last good line so that new records never follow garbage.
/// </para>
/// <para>
/// If writing or flushing fails (for example the disk is full) the journal tries to cut the file back to the last whole record
/// and becomes faulted: every later append throws, so the file never holds records that a resume would refuse to read.
/// </para>
/// </summary>
public sealed class JobJournal : IDisposable
{
    internal const int MaxLineBytes = 64 * 1024;
    internal const string PartSuffix = ".ferrypart";
    private const int FlushEveryRecords = 64;
    private const long FlushEveryMilliseconds = 2000;
    private static readonly byte[] CrcPrefix = "{\"crc\":"u8.ToArray();
    private static readonly byte[] RecordPrefix = ",\"r\":"u8.ToArray();

    /// <summary>
    /// <see cref="JournalRecord.Detail"/> of a <see cref="JournalRecordKind.FileDone"/> record whose file was renamed into place without being
    /// forced to disk first. After a power cut such a file may hold blanks or old data, so a resume checks it again before trusting it.
    /// </summary>
    public const string NotOnDiskYet = "unflushed";

    private readonly object _lock = new();
    private readonly FileStream _stream;
    private readonly HashSet<string> _completed;
    private readonly HashSet<string> _notOnDisk = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<JournalPart> _parts;
    private readonly List<JournalPartIdentity> _identities;
    private readonly TimeProvider _time;
    private readonly JournalIo _io;
    private long _goodOffset;
    private int _unflushed;
    private long _lastFlushTimestamp;
    private bool _disposed;
    private Exception? _fault;
    private volatile bool _finished;

    private JobJournal(
        FileStream stream, string optionsJson, HashSet<string> completed, List<JournalPart> parts, bool isFinished, TimeProvider time, JournalIo io,
        HashSet<string>? notOnDisk = null, List<JournalPartIdentity>? identities = null)
    {
        _stream = stream;
        _identities = identities ?? [];
        OptionsJson = optionsJson;
        _completed = completed;
        if (notOnDisk is not null)
        {
            _notOnDisk.UnionWith(notOnDisk);
        }

        _parts = parts;
        _finished = isFinished;
        _time = time;
        _io = io;
        _goodOffset = stream.Position;
        _lastFlushTimestamp = time.GetTimestamp();
    }

    /// <summary>The options the job was started with.</summary>
    public string OptionsJson { get; }

    /// <summary>When the job started (from the header), for a reopened journal; default for a new one.</summary>
    public DateTime StartedUtc { get; private init; }

    /// <summary>
    /// For a reopened journal: when the file was last written before this open, roughly when the job stopped (Windows may update it late, so
    /// it can be early, never late). Null for a new journal. A resume uses it to tell files written during the job from files changed after.
    /// </summary>
    public DateTime? LastActivityUtc { get; private init; }

    /// <summary>True once a <see cref="JournalRecordKind.JobFinished"/> record has been read or appended.</summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// A snapshot (a copy) of the relative paths of every <see cref="JournalRecordKind.FileDone"/> record, compared ignoring case
    /// because Windows paths are, with <c>/</c> treated as <c>\</c>. Intended to be read once at resume; do not call it per file.
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

    /// <summary>
    /// A snapshot of the <see cref="CompletedPaths"/> whose latest <see cref="JournalRecordKind.FileDone"/> record says
    /// <see cref="NotOnDiskYet"/>. A resume must check these files again instead of trusting them.
    /// </summary>
    public IReadOnlySet<string> NotOnDiskPaths
    {
        get
        {
            lock (_lock)
            {
                return new HashSet<string>(_notOnDisk, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Records a FileDone line in both sets; a later done record for the same file replaces the earlier one's flag.</summary>
    private static void TrackDone(HashSet<string> completed, HashSet<string> notOnDisk, JournalRecord record)
    {
        var key = Normalize(record.RelativePath!);
        completed.Add(key);
        if (string.Equals(record.Detail, NotOnDiskYet, StringComparison.Ordinal))
        {
            notOnDisk.Add(key);
        }
        else
        {
            notOnDisk.Remove(key);
        }
    }

    /// <summary>A snapshot (a copy) of every part file named by a <see cref="JournalRecordKind.FilePartStarted"/> record, in order.</summary>
    public IReadOnlyList<JournalPart> PartFiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _parts];
            }
        }
    }

    /// <summary>A snapshot of every part-file identity named by a <see cref="JournalRecordKind.FilePartCreated"/> record, in order.</summary>
    public IReadOnlyList<JournalPartIdentity> PartIdentities
    {
        get
        {
            lock (_lock)
            {
                return [.. _identities];
            }
        }
    }

    /// <summary>The <see cref="JournalRecord.Hash"/> text of a <see cref="JournalRecordKind.FilePartCreated"/> record: volume serial and file id in hex.</summary>
    public static string FormatIdentity(ulong volumeSerial, UInt128 fileId) =>
        volumeSerial.ToString("x16", CultureInfo.InvariantCulture) + "-" + fileId.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>Reads text made by <see cref="FormatIdentity"/>; anything else is refused.</summary>
    internal static bool TryParseIdentity(string? text, out ulong volumeSerial, out UInt128 fileId)
    {
        volumeSerial = 0;
        fileId = UInt128.Zero;
        return text is { Length: 49 } && text[16] == '-'
            && IsLowerHex(text.AsSpan(0, 16)) && IsLowerHex(text.AsSpan(17))
            && ulong.TryParse(text.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out volumeSerial)
            && UInt128.TryParse(text.AsSpan(17), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out fileId);

        static bool IsLowerHex(ReadOnlySpan<char> s) => !s.ContainsAnyExcept(LowerHex);
    }

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>Only called for a record that passed <see cref="Validate"/>, so its identity text is well formed.</summary>
    private static JournalPartIdentity IdentityOf(JournalRecord record) =>
        TryParseIdentity(record.Hash, out var volume, out var id)
            ? new JournalPartIdentity(Normalize(record.RelativePath!), Normalize(record.Detail!), volume, id)
            : throw new InvalidOperationException("A part identity that passed validation could not be read.");

    /// <summary>Starts a new journal. Throws <see cref="IOException"/> if one already exists for this job.</summary>
    public static JobJournal Create(string jobsDir, Guid jobId, string optionsJson, TimeProvider? timeProvider = null) =>
        CreateWithIo(jobsDir, jobId, optionsJson, timeProvider, null);

    internal static JobJournal CreateWithIo(string jobsDir, Guid jobId, string optionsJson, TimeProvider? timeProvider, JournalIo? io)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);
        ArgumentNullException.ThrowIfNull(optionsJson);

        Directory.CreateDirectory(jobsDir);
        var stream = new FileStream(JournalPath(jobsDir, jobId), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024);
        try
        {
            var journal = new JobJournal(stream, optionsJson, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], false,
                timeProvider ?? TimeProvider.System, io ?? new JournalIo());
            var header = new JournalRecord(JournalRecordKind.JobStarted, null, 0, DateTime.UtcNow, null, optionsJson);
            journal.AppendCore(header, isHeader: true, forceFlush: true);
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
    public static JobJournal Open(string jobsDir, Guid jobId, TimeProvider? timeProvider = null) =>
        OpenWithIo(jobsDir, jobId, timeProvider, null);

    /// <summary>Reads valid journal records for history without changing a damaged tail or locking out a running job.</summary>
    public static JournalSummary ReadSummary(string jobsDir, Guid jobId, CancellationToken ct = default)
    {
        using var stream = new FileStream(JournalPath(jobsDir, jobId), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var load = Load(stream, collectFileState: false, ct);
        if (load.Options is null)
        {
            throw new InvalidDataException("The saved job could not be read.");
        }

        return new JournalSummary(load.Options, load.StartedUtc, load.Finished, load.Result);
    }

    internal static JobJournal OpenWithIo(string jobsDir, Guid jobId, TimeProvider? timeProvider, JournalIo? io)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobsDir);

        var path = JournalPath(jobsDir, jobId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The job journal for this job was not found, so it cannot be resumed.", path);
        }

        var lastActivity = File.GetLastWriteTimeUtc(path); // before this open changes it
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
            return new JobJournal(stream, load.Options, load.Completed, load.Parts, load.Finished, timeProvider ?? TimeProvider.System, io ?? new JournalIo(), load.NotOnDisk,
                load.Identities)
            {
                StartedUtc = load.StartedUtc,
                LastActivityUtc = lastActivity,
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds a record. Safe to call from several threads. Throws <see cref="ArgumentException"/> (and writes nothing) for a record
    /// that a resume would refuse to read. Flushing follows the policy in the class summary: the 2-second rule is checked
    /// only on this call, so an idle tail of up to 63 records can stay buffered until the next call or dispose.
    /// </summary>
    public void Append(JournalRecord record) => AppendCore(record, isHeader: false, forceFlush: false);

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
                if (_fault is null)
                {
                    _io.Flush(_stream);
                }
            }
            finally
            {
                try
                {
                    _stream.Dispose();
                }
                catch (IOException) when (_fault is not null)
                {
                    // The journal already failed and was cut back; a second failure while closing adds nothing.
                }
            }
        }
    }

    /// <summary>
    /// The single rule set for a record, used both when appending and when reading a file back, so the two can never disagree.
    /// The header must be the first record and the only <see cref="JournalRecordKind.JobStarted"/>.
    /// </summary>
    internal static bool Validate(JournalRecord record, bool isHeader)
    {
        if (!Enum.IsDefined(record.Kind))
        {
            return false;
        }

        if (isHeader)
        {
            return record.Kind == JournalRecordKind.JobStarted && record.Detail is not null && record.RelativePath is null;
        }

        if (record.Kind != JournalRecordKind.FilePartStarted && record.TargetPath is not null)
        {
            return false;
        }

        switch (record.Kind)
        {
            case JournalRecordKind.JobStarted:
                return false;
            case JournalRecordKind.FileDone:
            case JournalRecordKind.FileSkipped:
            case JournalRecordKind.FileFailed:
                return record.RelativePath is not null && IsSafeRelativePath(record.RelativePath);
            case JournalRecordKind.FilePartStarted:
                return record.RelativePath is not null && IsSafeRelativePath(record.RelativePath)
                    && record.Detail is not null && IsSafeRelativePath(record.Detail)
                    && record.Detail.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
                    && record.TargetPath is not null && IsSafeRelativePath(record.TargetPath)
                    && string.Equals(FolderOf(record.Detail), FolderOf(record.TargetPath), StringComparison.OrdinalIgnoreCase);
            case JournalRecordKind.FilePartCreated:
                return record.RelativePath is not null && IsSafeRelativePath(record.RelativePath)
                    && record.Detail is not null && IsSafeRelativePath(record.Detail)
                    && record.Detail.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
                    && TryParseIdentity(record.Hash, out _, out _);
            default: // JobFinished
                return record.RelativePath is null || IsSafeRelativePath(record.RelativePath);
        }
    }

    private void AppendCore(JournalRecord record, bool isHeader, bool forceFlush)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Validate(record, isHeader))
        {
            throw new ArgumentException("This record is not valid for the journal and was not written.", nameof(record));
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
            if (_fault is not null)
            {
                throw new InvalidOperationException("The journal is faulted after an earlier write failure and cannot take more records.", _fault);
            }

            try
            {
                _io.Write(_stream, line);

                var mustFlush = forceFlush
                    || record.Kind is JournalRecordKind.JobFinished or JournalRecordKind.FileFailed
                    || _unflushed + 1 >= FlushEveryRecords
                    || _time.GetElapsedTime(_lastFlushTimestamp).TotalMilliseconds >= FlushEveryMilliseconds;

                if (mustFlush)
                {
                    _io.Flush(_stream);
                    _unflushed = 0;
                    _lastFlushTimestamp = _time.GetTimestamp();
                }
                else
                {
                    _unflushed++;
                    if (record.Kind == JournalRecordKind.FilePartStarted)
                    {
                        // The part file is created right after this returns: make sure a crashed process still leaves its name behind.
                        _io.HandOver(_stream);
                    }
                }
            }
            catch (Exception ex)
            {
                Fault(ex);
                throw;
            }

            _goodOffset += line.Length;
            if (record.Kind == JournalRecordKind.FileDone)
            {
                TrackDone(_completed, _notOnDisk, record);
            }
            else if (record.Kind == JournalRecordKind.FilePartStarted)
            {
                _parts.Add(new JournalPart(Normalize(record.RelativePath!), Normalize(record.Detail!), Normalize(record.TargetPath!)));
            }
            else if (record.Kind == JournalRecordKind.FilePartCreated)
            {
                _identities.Add(IdentityOf(record));
            }
            else if (record.Kind == JournalRecordKind.JobFinished)
            {
                _finished = true;
            }
        }
    }

    /// <summary>Cuts the file back to the end of the last whole record (best effort) and refuses all later appends.</summary>
    private void Fault(Exception cause)
    {
        _fault = cause;
        try
        {
            _stream.SetLength(_goodOffset);
            _stream.Seek(_goodOffset, SeekOrigin.Begin);
            _stream.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            // Could not clean up either. Open() still stops at the partial line, so nothing untrusted is ever read.
        }
    }

    private static string JournalPath(string jobsDir, Guid jobId) =>
        Path.Combine(jobsDir, jobId.ToString("N") + ".journal");

    private static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>The folder part of a relative path ("" for a bare name), with either slash.</summary>
    private static string FolderOf(string path)
    {
        var normal = Normalize(path);
        var cut = normal.LastIndexOf('\\');
        return cut < 0 ? string.Empty : normal[..cut];
    }

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

    private readonly record struct LoadResult(string? Options, HashSet<string> Completed, List<JournalPart> Parts, bool Finished, long GoodLength,
        DateTime StartedUtc, string? Result, HashSet<string> NotOnDisk, List<JournalPartIdentity> Identities);

    /// <summary>Reads lines from the start and stops at the first one that cannot be trusted.</summary>
    private static LoadResult Load(FileStream stream, bool collectFileState = true, CancellationToken ct = default)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<JournalPart>();
        var identities = new List<JournalPartIdentity>();
        string? options = null;
        var finished = false;
        var startedUtc = default(DateTime);
        string? result = null;
        long goodLength = 0;

        stream.Seek(0, SeekOrigin.Begin);
        var chunk = new byte[16 * 1024];
        var line = new byte[MaxLineBytes];
        var lineLength = 0;
        long consumed = 0;
        var stop = false;

        while (!stop)
        {
            ct.ThrowIfCancellationRequested();
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

                var isHeader = options is null;
                if (!TryParseLine(line.AsSpan(0, lineLength), isHeader, out var record))
                {
                    stop = true;
                    continue;
                }

                switch (record.Kind)
                {
                    case JournalRecordKind.JobStarted:
                        options = record.Detail;
                        startedUtc = record.SourceLastWriteUtc;
                        break;
                    case JournalRecordKind.FileDone:
                        if (collectFileState)
                        {
                            TrackDone(completed, notOnDisk, record);
                        }
                        break;
                    case JournalRecordKind.JobFinished:
                        finished = true;
                        result = record.Detail;
                        break;
                    case JournalRecordKind.FilePartStarted:
                        if (collectFileState)
                        {
                            parts.Add(new JournalPart(Normalize(record.RelativePath!), Normalize(record.Detail!), Normalize(record.TargetPath!)));
                        }
                        break;
                    case JournalRecordKind.FilePartCreated:
                        if (collectFileState)
                        {
                            identities.Add(IdentityOf(record));
                        }
                        break;
                    default:
                        break;
                }

                goodLength = consumed;
                lineLength = 0;
            }
        }

        return new LoadResult(options, completed, parts, finished, goodLength, startedUtc, result, notOnDisk, identities);
    }

    private static bool TryParseLine(ReadOnlySpan<byte> line, bool isHeader, out JournalRecord record)
    {
        record = null!;

        // Strict shape: {"crc":<digits>,"r":<json object>}. A trailing CR (CRLF file) fails the final-brace check.
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

        if (parsed is null || !Validate(parsed, isHeader))
        {
            return false;
        }

        record = parsed;
        return true;
    }

    /// <summary>
    /// Accepts only plain relative paths. Rejects empty paths, control characters (including NUL), <c>:</c> anywhere (drive letters,
    /// alternate data streams, <c>\\?\</c> prefixes), rooted paths, and any segment that is <c>.</c> or <c>..</c> once Windows' trimming of
    /// trailing spaces and dots is applied (so <c>.. </c> and <c>...</c> count too). A tampered journal can therefore never point outside the destination.
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c < ' ' || c == ':')
            {
                return false;
            }
        }

        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment.Length > 0 && segment.AsSpan().TrimEnd(" .").IsEmpty)
            {
                return false;
            }
        }

        return true;
    }
}
