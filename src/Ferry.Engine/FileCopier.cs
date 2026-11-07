using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>
/// Copies one file. Data goes to a part file, <c>&lt;destination&gt;.&lt;random&gt;.ferrypart</c>, and only takes the real name (by rename) once it is complete, flushed,
/// the right size, and the source did not change meanwhile; the real name never holds partial content. Every failure or cancellation
/// deletes the part file. Large files stream through a reader loop and a writer loop joined by a small bounded queue of pooled, aligned buffers.
/// Thread safe: one instance can run many copies at once.
/// </summary>
public sealed class FileCopier
{
    public const string PartSuffix = ".ferrypart";

    /// <summary>Files at least this big skip the OS cache on both sides (tuned by benchmarks).</summary>
    public const long UnbufferedThresholdBytes = 8L * 1024 * 1024;

    /// <summary>Chunk size used when <see cref="CopyOptions.BufferSizeBytes"/> is 0 (tuned by benchmarks).</summary>
    public const int DefaultBufferSizeBytes = 4 * 1024 * 1024;

    public const int MaxBufferSizeBytes = 256 * 1024 * 1024;

    /// <summary>Chunks that may wait between the reader and the writer.</summary>
    internal const int PipelineDepth = 4;

    private const int BufferedAlignment = 4096;

    internal const FileAttributes CopiedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFileSystem _fs;
    private readonly BufferPool _pool;
    private readonly TimeProvider _time;

    public FileCopier(IFileSystem fs, BufferPool pool)
        : this(fs, pool, TimeProvider.System)
    {
    }

    /// <summary>Lets tests supply a clock for the speed limit and progress timing.</summary>
    public FileCopier(IFileSystem fs, BufferPool pool, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(time);
        _fs = fs;
        _pool = pool;
        _time = time;
    }

    /// <summary>The longest file name Windows allows in one path segment.</summary>
    internal const int MaxNameLength = 255;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, which must not exist yet (conflict rules belong to the caller).
    /// Disk failures come back as an unsuccessful outcome with a <see cref="Problem"/>; cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<FileCopyOutcome> CopyAsync(
        string source, string destination, CopyOptions options, PauseGate pause, IProgress<FileProgress> progress, CancellationToken ct) =>
        CopyAsync(source, destination, options, pause, progress, null, ct);

    /// <summary>
    /// As the overload without hooks; <paramref name="hooks"/> lets a job learn each part-file name before it is created, check or extend the
    /// complete part file before it takes the real name, replace an existing destination, or try block cloning first.
    /// A link to a file (symbolic link) is copied as the contents of the file it points to.
    /// </summary>
    public async Task<FileCopyOutcome> CopyAsync(
        string source, string destination, CopyOptions options, PauseGate pause, IProgress<FileProgress> progress, FileCopyHooks? hooks, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BufferSizeBytes);
        if (options.SpeedLimitBytesPerSecond is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        }

        string? part = null;
        IReadHandle? reader = null;
        IWriteHandle? writer = null;
        StreamingHasher? hasher = null;
        var partIsOurs = false;
        try
        {
            await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            var before = _fs.GetEntryFollowingLinks(source);
            var length = before.Length;
            if (hooks is { TryBlockClone: true } && length > 0)
            {
                part = TryClone(source, destination, hooks);
                if (part is not null)
                {
                    partIsOurs = true;
                    var cloned = await FinishCloneAsync(source, destination, options, hooks, before, part, progress, ct).ConfigureAwait(false);
                    partIsOurs = !cloned.Success;
                    return cloned;
                }
            }

            var unbuffered = length >= UnbufferedThresholdBytes;
            var alignment = unbuffered ? Math.Max(_fs.GetSectorSize(source), _fs.GetSectorSize(destination)) : BufferedAlignment;
            reader = _fs.OpenRead(source, unbuffered);
            if (reader.Length != length)
            {
                return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part);
            }

            (writer, part) = CreatePart(destination, length, unbuffered, hooks);
            partIsOurs = true;
            if (options.Verify || options.ExportChecksums)
            {
                hasher = new StreamingHasher(options.HashAlgorithm);
            }

            var transfer = new Transfer(
                reader,
                writer,
                _pool,
                _time,
                hasher,
                options.SpeedLimitBytesPerSecond is { } rate ? new SpeedLimiter(rate, _time) : null,
                pause,
                progress,
                Path.GetFileName(source),
                length,
                ChunkSizeFor(options, alignment),
                alignment,
                unbuffered);
            await transfer.RunAsync(ct).ConfigureAwait(false);

            // All data is written. Make it durable and close it; any failure here fails the copy (never rename after a failed flush).
            await writer.FlushAsync().ConfigureAwait(false);
            var w = writer;
            writer = null;
            await w.DisposeAsync().ConfigureAwait(false);
            var r = reader;
            reader = null;
            await r.DisposeAsync().ConfigureAwait(false);

            if (CheckComplete(source, before, part, length) is { } incomplete)
            {
                return incomplete;
            }

            var hash = hasher?.Finish();
            if (await RunBeforeCommitAsync(hooks, part, hash, ct).ConfigureAwait(false) is { } refused)
            {
                return new FileCopyOutcome(false, null, refused, part);
            }

            ct.ThrowIfCancellationRequested();
            Commit(part, destination, hooks);
            partIsOurs = false;
            ReportFinalProgress(transfer);

            // After the rename (a rename can change them), and ReadOnly last so it cannot block the rename.
            var warning = options.KeepDatesAndAttributes ? ApplyMetadata(source, destination, before) : null;
            return new FileCopyOutcome(true, hash, warning, part);
        }
        catch (SourceChangedException)
        {
            return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part);
        }
        catch (FileSystemException ex) when (!ct.IsCancellationRequested)
        {
            var atPart = part is not null && SamePath(ex.Path, part);
            var message = atPart && ex.Kind == IoErrorKind.NotFound
                ? CopyMessages.PartFileVanished
                : CopyMessages.For(ex.Kind, atPart || SamePath(ex.Path, destination), DriveOf(destination));
            return Failed(source, ex.Kind, message, part);
        }
        catch (FileSystemException ex)
        {
            throw new OperationCanceledException("The copy was cancelled.", ex, ct);
        }
        finally
        {
            await CleanUpAsync(reader, writer, partIsOurs ? part : null).ConfigureAwait(false);
            hasher?.Dispose();
        }
    }

    /// <summary>The source must be unchanged and the part file exactly the right size before anything else happens to it.</summary>
    private FileCopyOutcome? CheckComplete(string source, FileEntry before, string part, long length)
    {
        var after = _fs.GetEntryFollowingLinks(source);
        if (after.Length != length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part);
        }

        return _fs.GetEntry(part).Length != length ? Failed(source, IoErrorKind.Other, CopyMessages.WrongSize, part) : null;
    }

    private static async ValueTask<Problem?> RunBeforeCommitAsync(FileCopyHooks? hooks, string part, byte[]? hash, CancellationToken ct) =>
        hooks?.BeforeCommit is { } check ? await check(part, hash, ct).ConfigureAwait(false) : null;

    /// <summary>
    /// Gives the complete part file its real name. When replacing, an existing ReadOnly destination has ReadOnly cleared first (restored if the
    /// replace then fails); the old file is only ever replaced by the rename itself, never deleted beforehand.
    /// </summary>
    private void Commit(string part, string destination, FileCopyHooks? hooks)
    {
        if (hooks is not { ReplaceExisting: true })
        {
            _fs.Move(part, destination, overwrite: false);
            return;
        }

        FileEntry? old = null;
        try
        {
            old = _fs.GetEntry(destination);
        }
        catch (FileSystemException ex) when (ex.Kind == IoErrorKind.NotFound)
        {
            // Gone meanwhile: a plain rename will do.
        }

        var clearedReadOnly = false;
        if (old is not null && old.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            _fs.SetMetadata(destination, old.Attributes & ~FileAttributes.ReadOnly, old.CreationTimeUtc, old.LastWriteTimeUtc);
            clearedReadOnly = true;
        }

        try
        {
            _fs.Move(part, destination, overwrite: old is not null);
        }
        catch (FileSystemException) when (clearedReadOnly)
        {
            TryRestore(destination, old!);
            throw;
        }
    }

    private void TryRestore(string destination, FileEntry old)
    {
        try
        {
            _fs.SetMetadata(destination, old.Attributes, old.CreationTimeUtc, old.LastWriteTimeUtc);
        }
        catch (FileSystemException)
        {
            // Best effort: the old file is intact either way, only its ReadOnly flag may be off.
        }
    }

    /// <summary>Tries block cloning onto a fresh part name (announced first). Returns the part path on success, null to copy normally.</summary>
    private string? TryClone(string source, string destination, FileCopyHooks hooks)
    {
        var part = NewPartPath(destination);
        hooks.PartChosen?.Invoke(part);
        return _fs.TryCloneBlocks(source, part) ? part : null;
    }

    /// <summary>The clone already holds the data: hash the source if asked, then the same checks and commit as a normal copy.</summary>
    private async Task<FileCopyOutcome> FinishCloneAsync(
        string source, string destination, CopyOptions options, FileCopyHooks hooks, FileEntry before, string part, IProgress<FileProgress> progress, CancellationToken ct)
    {
        byte[]? hash = null;
        if (options.Verify || options.ExportChecksums)
        {
            hash = await Verifier.HashAsync(_fs, source, unbuffered: false, options.HashAlgorithm, ct).ConfigureAwait(false);
        }

        if (CheckComplete(source, before, part, before.Length) is { } incomplete)
        {
            return incomplete;
        }

        if (await RunBeforeCommitAsync(hooks, part, hash, ct).ConfigureAwait(false) is { } refused)
        {
            return new FileCopyOutcome(false, null, refused, part);
        }

        ct.ThrowIfCancellationRequested();
        Commit(part, destination, hooks);
#pragma warning disable CA1031 // The file is complete; a failing callback must not turn it into a failure.
        try
        {
            progress.Report(new FileProgress(Path.GetFileName(source), before.Length, before.Length));
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        var warning = options.KeepDatesAndAttributes ? ApplyMetadata(source, destination, before) : null;
        return new FileCopyOutcome(true, hash, warning, part);
    }

    private static FileCopyOutcome Failed(string source, IoErrorKind kind, string message, string? part) =>
        new(false, null, new Problem(source, kind, message), part);

    /// <summary>The copy is already complete under its real name, so a failing progress callback must not turn it into a failure.</summary>
    private static void ReportFinalProgress(Transfer transfer)
    {
#pragma warning disable CA1031 // The callback belongs to the caller; the file is done whatever it throws.
        try
        {
            transfer.ReportProgress(force: true);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static int ChunkSizeFor(CopyOptions options, int alignment)
    {
        long size = options.BufferSizeBytes > 0 ? options.BufferSizeBytes : DefaultBufferSizeBytes;
        if (options.SpeedLimitBytesPerSecond is { } limit)
        {
            size = Math.Min(size, limit / 4); // keeps each burst short when the limit is low
        }

        size = Math.Clamp(size, alignment, MaxBufferSizeBytes);
        return (int)AlignUp(size, alignment);
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static string DriveOf(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(PathDisplay.ToDisplayPath(destination));
            return string.IsNullOrEmpty(root) ? destination : root.TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return destination;
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + p[8..];
        }

        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    /// <summary>
    /// Creates this attempt's part file, <c>&lt;destination&gt;.&lt;8 random hex&gt;.ferrypart</c>. The random name means a concurrent job or a
    /// crashed earlier attempt can never share it, and nothing that already exists is ever deleted or reused (stale parts are cleaned up from
    /// the job journal). A name clash is retried with a fresh name, up to three times.
    /// </summary>
    private (IWriteHandle Handle, string Path) CreatePart(string destination, long length, bool unbuffered, FileCopyHooks? hooks)
    {
        const int MaxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            var part = NewPartPath(destination);
            hooks?.PartChosen?.Invoke(part); // the job journals the name before the file exists
            try
            {
                return (_fs.CreateWrite(part, length, unbuffered), part);
            }
            catch (FileSystemException ex) when (ex.Kind == IoErrorKind.AlreadyExists && attempt < MaxAttempts)
            {
                // Someone else's file has this name: leave it alone and pick another.
            }
        }
    }

    /// <summary>
    /// <c>&lt;destination&gt;.&lt;8 random hex&gt;.ferrypart</c> in the destination's own folder. When that name would pass 255 characters the
    /// destination's name is shortened inside the part name (never splitting a surrogate pair); the folder never changes.
    /// </summary>
    internal static string NewPartPath(string destination)
    {
        var suffix = "." + RandomNumberGenerator.GetHexString(8, lowercase: true) + PartSuffix;
        var cut = destination.LastIndexOfAny(['\\', '/']) + 1;
        var name = destination[cut..];
        var room = MaxNameLength - suffix.Length;
        if (name.Length > room)
        {
            var keep = room;
            if (char.IsHighSurrogate(name[keep - 1]))
            {
                keep--;
            }

            name = name[..keep];
        }

        return string.Concat(destination.AsSpan(0, cut), name, suffix);
    }

    private Problem? ApplyMetadata(string source, string destination, FileEntry before)
    {
        var attributes = before.Attributes & CopiedAttributes;
        try
        {
            _fs.SetMetadata(destination, attributes == 0 ? FileAttributes.Normal : attributes, before.CreationTimeUtc, before.LastWriteTimeUtc);
            return null;
        }
        catch (FileSystemException ex)
        {
            return new Problem(source, ex.Kind, CopyMessages.DatesAndAttributesNotKept);
        }
    }

    /// <summary>Closes whatever is still open and deletes our part file. Never throws, so it cannot hide the original error.</summary>
    private async ValueTask CleanUpAsync(IReadHandle? reader, IWriteHandle? writer, string? part)
    {
#pragma warning disable CA1031 // Cleanup after a failure must not replace that failure with its own.
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (reader is not null)
        {
            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (part is not null)
        {
            try
            {
                _fs.Delete(part);
            }
            catch (Exception)
            {
            }
        }
#pragma warning restore CA1031
    }

    private readonly record struct Chunk(AlignedBuffer Buffer, long Offset, int Length);

    /// <summary>The source ended early or grew while it was read.</summary>
    private sealed class SourceChangedException : Exception
    {
        public SourceChangedException()
            : base(CopyMessages.SourceChanged)
        {
        }
    }

    /// <summary>State of one copy's data transfer. The read side and the write side each touch only their own fields.</summary>
    private sealed class Transfer(
        IReadHandle reader,
        IWriteHandle writer,
        BufferPool pool,
        TimeProvider time,
        StreamingHasher? hasher,
        SpeedLimiter? limiter,
        PauseGate pause,
        IProgress<FileProgress> progress,
        string name,
        long length,
        int chunkSize,
        int alignment,
        bool unbuffered)
    {
        private Exception? _firstFailure; // whichever loop faulted first
        private long _done; // write side
        private long _lastReport = time.GetTimestamp(); // write side

        public async Task RunAsync(CancellationToken ct)
        {
            if (length == 0)
            {
                return;
            }

            if (length <= chunkSize)
            {
                await CopySingleChunkAsync(ct).ConfigureAwait(false);
                return;
            }

            await RunPipelineAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Reports progress at most every <see cref="ProgressInterval"/> unless forced (keeps the per-chunk path allocation free).</summary>
        public void ReportProgress(bool force)
        {
            var now = time.GetTimestamp();
            if (!force && time.GetElapsedTime(_lastReport, now) < ProgressInterval)
            {
                return;
            }

            _lastReport = now;
            progress.Report(new FileProgress(name, _done, length));
        }

        /// <summary>Small files: one read, one write, no queue or extra tasks.</summary>
        private async Task CopySingleChunkAsync(CancellationToken ct)
        {
            var count = (int)length;
            var buffer = pool.Rent((int)AlignUp(count, alignment));
            try
            {
                await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                await ReadChunkAsync(buffer, 0, count, ct).ConfigureAwait(false);
                if (limiter is not null)
                {
                    await limiter.WaitAsync(count, ct).ConfigureAwait(false);
                }

                await WriteChunkAsync(new Chunk(buffer, 0, count), ct).ConfigureAwait(false);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        private async Task RunPipelineAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(PipelineDepth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            // Channel calls take no token so they reuse their internal wait objects (no allocation per chunk). Cancelling closes the channel instead.
            using var registration = cts.Token.Register(
                static s => ((ChannelWriter<Chunk>)s!).TryComplete(new OperationCanceledException()), channel.Writer);
            var readTask = ReadLoopAsync(channel.Writer, cts);
            var writeTask = WriteLoopAsync(channel.Reader, cts);
            await Task.WhenAll(readTask, writeTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // Both loops have stopped: give back any chunk still queued.
            while (channel.Reader.TryRead(out var left))
            {
                pool.Return(left.Buffer);
            }

            if (readTask.IsCompletedSuccessfully && writeTask.IsCompletedSuccessfully)
            {
                return;
            }

            // Report the failure that started it, not the cancellation (or aborted I/O) it then caused on the other side.
            var first = Volatile.Read(ref _firstFailure);
            var primary = IsReal(first) ? first : RealFailure(readTask) ?? RealFailure(writeTask);
            if (primary is not null)
            {
                ExceptionDispatchInfo.Throw(primary);
            }

            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }

        private static Exception? RealFailure(Task task) => IsReal(task.Exception?.InnerException) ? task.Exception!.InnerException : null;

        private static bool IsReal(Exception? e) => e is not null and not OperationCanceledException and not ChannelClosedException;

        private void RecordFailure(Exception ex) => Interlocked.CompareExchange(ref _firstFailure, ex, null);

        private async Task ReadLoopAsync(ChannelWriter<Chunk> output, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            AlignedBuffer? buffer = null;
            try
            {
                for (long offset = 0; offset < length;)
                {
                    await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    var count = (int)Math.Min(chunkSize, length - offset);
                    buffer = pool.Rent(chunkSize);
                    await ReadChunkAsync(buffer, offset, count, ct).ConfigureAwait(false);
                    if (limiter is not null)
                    {
                        await limiter.WaitAsync(count, ct).ConfigureAwait(false);
                    }

                    await output.WriteAsync(new Chunk(buffer, offset, count)).ConfigureAwait(false);
                    buffer = null; // the write side owns it now
                    offset += count;
                }

                output.TryComplete();
            }
            catch (Exception ex)
            {
                if (buffer is not null)
                {
                    pool.Return(buffer);
                }

                RecordFailure(ex);
                output.TryComplete(ex);
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Design note for Task 16 (ruling R27): this loop keeps ONE write in flight per file. To raise it, keep up to N WriteChunkAsync
        // calls outstanding (e.g. a SemaphoreSlim(N) or a small ring of pending tasks) and return each chunk's buffer when its own write
        // completes. The change is local: every chunk carries its own offset, so writes may complete in any order. Two rules must hold:
        // (1) the final short (unaligned) tail write is issued only after every earlier write has completed, because the handle zero-pads
        // it to a whole sector and trims the file to the data end on flush; (2) progress adds bytes as each write completes (Interlocked)
        // and never assumes in-order completion. FileOptions.WriteThrough on unbuffered handles is not needed for safety:
        // FlushFileBuffers runs before the rename, so the real name only ever appears on durable data.
        private async Task WriteLoopAsync(ChannelReader<Chunk> input, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            try
            {
                while (await input.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (input.TryRead(out var chunk))
                    {
                        try
                        {
                            await WriteChunkAsync(chunk, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            pool.Return(chunk.Buffer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Fills <paramref name="count"/> bytes (reads may come back short, so it loops) and hashes exactly those bytes, in order.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask ReadChunkAsync(AlignedBuffer buffer, long offset, int count, CancellationToken ct)
        {
            // Unbuffered reads ask for whole sectors (the end of file just returns fewer bytes).
            var memory = buffer.Memory[..(unbuffered ? (int)AlignUp(count, alignment) : count)];
            var filled = 0;
            while (filled < count)
            {
                var n = await reader.ReadAsync(memory[filled..], offset + filled, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new SourceChangedException(); // shorter than when the copy started
                }

                filled += n;
            }

            hasher?.Append(memory.Span[..count]);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask WriteChunkAsync(Chunk chunk, CancellationToken ct)
        {
            if (pause.IsPaused)
            {
                ReportProgress(force: true); // show where it stopped
            }

            await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            var data = chunk.Buffer.Memory[..chunk.Length];

            // Unbuffered: whole sectors go straight from the aligned buffer; only the final short tail is padded by the handle and trimmed on flush.
            var whole = unbuffered ? chunk.Length - (chunk.Length % alignment) : chunk.Length;
            if (whole > 0)
            {
                await writer.WriteAsync(data[..whole], chunk.Offset, ct).ConfigureAwait(false);
            }

            if (whole < chunk.Length)
            {
                await writer.WriteAsync(data[whole..], chunk.Offset + whole, ct).ConfigureAwait(false);
            }

            _done += chunk.Length;
            ReportProgress(force: false);
        }
    }
}
