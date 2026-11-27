using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;
using EveryApp.Core.Paths;

namespace Ferry.Engine;

/// <summary>
/// Copies one file. Data goes to a part file, <c>&lt;destination&gt;.&lt;random&gt;.ferrypart</c>, and only takes the real name (by rename) once it is complete, flushed,
/// the right size, verified when asked, and the source did not change meanwhile; the real name never holds partial content. The one exception is
/// <see cref="FileCopyHooks.SkipSmallFileFlush"/>: a small file may then reach its real name before its data is on disk, so a power cut can leave
/// it blank, and the caller journals it (<see cref="FileCopyOutcome.Flushed"/>) to check it again on resume. Every failure or cancellation
/// deletes the part file. Large files keep several reads and several writes of pooled, aligned chunks in flight at once.
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

    /// <summary>Chunks one large file keeps in flight on each side: this many reads and this many writes at once.</summary>
    internal const int PipelineDepth = 4;

    private const int BufferedAlignment = 4096;

    internal const FileAttributes CopiedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFileSystem _fs;
    private readonly BufferPool _pool;
    private readonly TimeProvider _time;
    private readonly CopierTuning _tuning;

    public FileCopier(IFileSystem fs, BufferPool pool)
        : this(fs, pool, TimeProvider.System)
    {
    }

    /// <summary>Lets tests supply a clock for the speed limit and progress timing.</summary>
    public FileCopier(IFileSystem fs, BufferPool pool, TimeProvider time)
        : this(fs, pool, time, CopierTuning.Default)
    {
    }

    /// <summary>Lets the benchmarks try other pipeline depths and unbuffered thresholds. The app always uses <see cref="CopierTuning.Default"/>.</summary>
    internal FileCopier(IFileSystem fs, BufferPool pool, TimeProvider time, CopierTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tuning.PipelineDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(tuning.UnbufferedThresholdBytes);
        _fs = fs;
        _pool = pool;
        _time = time;
        _tuning = tuning;
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxLostSectors);
        if (options.SpeedLimitBytesPerSecond is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        }

        string? part = null;
        IReadHandle? reader = null;
        IWriteHandle? writer = null;
        StreamingHasher? hasher = null;
        var partIsOurs = false;
        var retry = new RetryPolicy(time: _time);
        LargeFileGates? gates = null; // set only for a large, plain copy that takes turns with the job's other large files
        var holdingTransfer = false;
        var holdingCheck = false;
        try
        {
            await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            var before = await retry.RunAsync(() => Task.FromResult(_fs.GetEntryFollowingLinks(source)), RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false);
            var length = before.Length;
            if (IsNotTheListedFile(hooks, before))
            {
                return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part) with { NoRetry = true };
            }

            if (hooks is { TryBlockClone: true } && length > 0)
            {
                // Accepted risk (as for the clone itself): the clone is made by name, so its source is checked by name here.
                if (hooks.SourceMustBeUnder is { } sourceRoot && !PathSafety.IsSameOrUnderFinal(sourceRoot, _fs.GetFinalPath(source)))
                {
                    return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part) with { NoRetry = true };
                }

                part = TryClone(source, destination, hooks);
                if (part is not null)
                {
                    partIsOurs = true;
                    var cloned = await FinishCloneAsync(source, destination, options, hooks, before, part, progress, ct).ConfigureAwait(false);
                    partIsOurs = !cloned.Success;
                    return cloned;
                }
            }

            var unbuffered = length >= _tuning.UnbufferedThresholdBytes;

            // A small file that will be verified is written unbuffered too, so it can be read back from the drive before its handle closes.
            var writeUnbuffered = unbuffered || (options.Verify && hooks is { VerifyInCopier: true } && !options.RescueMode && length > 0);
            int SectorOf(string path, string? root) => hooks?.SectorSizes is { } cache ? cache.Get(root, path) : _fs.GetSectorSize(path);
            var alignment = writeUnbuffered || options.RescueMode
                ? Math.Max(SectorOf(source, hooks?.SourceSectorRoot), SectorOf(destination, hooks?.DestinationSectorRoot))
                : BufferedAlignment;

            // A large file waits for its turn to copy before it opens anything or creates its part file; it keeps the turn until its part is
            // flushed and closed (retries that reopen the part stay inside it).
            if (unbuffered && !options.RescueMode && hooks?.LargeGates is { } shared)
            {
                gates = shared;
                await gates.Transfer.WaitAsync(ct).ConfigureAwait(false);
                holdingTransfer = true;
            }

            reader = await retry.RunAsync(() => Task.FromResult(_fs.OpenRead(source, unbuffered)), RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false);
            if (reader.Length != length)
            {
                return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part);
            }

            // The file opened must be the one listed, and inside its source folder: asked through this handle, never by name, so a file or
            // folder swapped after the listing (or between the look-up above and the open) is refused before anything is written.
            if (IsNotTheListedFile(hooks, reader.GetEntry())
                || (hooks?.SourceMustBeUnder is { } mustBeUnder && !PathSafety.IsSameOrUnderFinal(mustBeUnder, reader.GetFinalPath())))
            {
                return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part) with { NoRetry = true };
            }

            reader = new RetryReadHandle(reader, async () =>
            {
                // Checked on the handle just opened, not by name first: a file swapped in between a look-up and the open cannot pass.
                var reopened = _fs.OpenRead(source, unbuffered);
                try
                {
                    var current = reopened.GetEntry();
                    if (current.FileId != before.FileId || current.VolumeSerial != before.VolumeSerial || current.Length != length || current.LastWriteTimeUtc != before.LastWriteTimeUtc)
                    {
                        throw new SourceChangedException();
                    }

                    return reopened;
                }
                catch
                {
                    await reopened.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }, retry, hooks?.Status);

            (writer, part) = await retry.RunAsync(() => Task.FromResult(CreatePart(destination, length, writeUnbuffered, hooks)), RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false);
            partIsOurs = true;
            if (hooks?.MustBeUnder is { } root && !PathSafety.IsSameOrUnderFinal(root, writer.GetFinalPath()))
            {
                writer.DeleteOnClose(); // removed through this handle when it closes; the name is never looked up again
                partIsOurs = false;
                return Failed(source, IoErrorKind.InvalidName, CopyMessages.PartOutsideDestination, part);
            }
            var originalFinalPath = writer.GetFinalPath();
            var partIdentity = writer.GetIdentity();
            hooks?.PartCreated?.Invoke(part, partIdentity);
            var retryWriter = new RetryWriteHandle(writer, async () =>
            {
                var reopened = _fs.OpenWriteExisting(part, writeUnbuffered);
                try
                {
                    // Same place and the same file: a hard link to another file under the part's name has the right path but not the id.
                    if (!SamePath(originalFinalPath, reopened.GetFinalPath()) || reopened.GetIdentity() != partIdentity)
                    {
                        partIsOurs = false; // the name now points somewhere else: never delete through it
                        throw new FileSystemException(part, IoErrorKind.InvalidName, null);
                    }

                    return reopened;
                }
                catch
                {
                    await reopened.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }, retry, hooks?.Status, ct);
            writer = retryWriter;

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
                hooks?.SharedLimiter ?? (options.SpeedLimitBytesPerSecond is { } rate ? new SpeedLimiter(rate, _time) : null),
                pause,
                progress,
                Path.GetFileName(source),
                length,
                ChunkSizeFor(options, alignment),
                alignment,
                unbuffered,
                writeUnbuffered,
                options.RescueMode ? new RescueReader(_fs, SectorOf(source, hooks?.SourceSectorRoot), pause, options.MaxLostSectors) : null,
                _tuning.PipelineDepth);
            await transfer.RunAsync(ct).ConfigureAwait(false);

            // Verify a small file before closing it, through the same handle: opening a just-written file again waits milliseconds for
            // antivirus scanning, more than the whole copy. Its writes were unbuffered, so this read comes from the drive, not the cache.
            var hash = hasher?.Finish();
            var verifyHere = options.Verify && hooks is { VerifyInCopier: true };
            if (verifyHere && hash is null)
            {
                return Failed(source, IoErrorKind.Other, CopyMessages.VerifyMismatch, part) with { VerifyFailed = true }; // nothing to compare with: never pass it
            }

            var verifiedBeforeClose = false;
            if (verifyHere && !unbuffered && (writeUnbuffered || length == 0) && !retryWriter.Reopened)
            {
                hooks!.Verifying?.Invoke();
                try
                {
                    if (!await ReadBackMatchesAsync(writer, length, alignment, hash!, options.HashAlgorithm, ct).ConfigureAwait(false))
                    {
                        return Failed(source, IoErrorKind.Other, CopyMessages.VerifyMismatch, part) with { VerifyFailed = true };
                    }
                }
                catch (FileSystemException ex) when (!ct.IsCancellationRequested)
                {
                    return Failed(source, ex.Kind, CopyMessages.VerifyCouldNotRead, part) with { VerifyFailed = true };
                }

                verifiedBeforeClose = true;
            }

            // All data is written. Make it durable and close it; any failure here fails the copy (never rename after a failed flush).
            // A caller that can recheck after a power cut may skip the flush for small, plain copies (it is most of their time); closing the
            // handle still writes the data out through the cache. Large, rescued and reconnected copies are always flushed.
            var flushed = !(hooks is { SkipSmallFileFlush: true } && !unbuffered && !options.RescueMode && !retryWriter.Reopened);
            if (flushed)
            {
                await writer.FlushAsync().ConfigureAwait(false);
            }

            var w = writer;
            writer = null;
            await w.DisposeAsync().ConfigureAwait(false);
            var r = reader;
            reader = null;
            await r.DisposeAsync().ConfigureAwait(false);

            // The data is written and closed: the next large file may start copying while this one is checked.
            if (holdingTransfer)
            {
                holdingTransfer = false;
                gates!.Transfer.Release();
            }

            if (await retry.RunAsync(() => Task.FromResult(CheckComplete(source, before, part, length)),
                RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false) is { } incomplete)
            {
                return incomplete;
            }

            if (retryWriter.Reopened)
            {
                var expected = hash ?? await retry.RunAsync(
                    () => HashRescuedSourceAsync(source, length, transfer.Lost, options.HashAlgorithm, ct), RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false);
                if (!await retry.RunAsync(
                    () => Verifier.MatchesAsync(_fs, part, expected, options.HashAlgorithm, ct), RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false))
                {
                    return Failed(source, IoErrorKind.Other, CopyMessages.VerifyMismatch, part) with { NoRetry = true };
                }

                if (await retry.RunAsync(() => Task.FromResult(CheckComplete(source, before, part, length)),
                    RetryPolicy.IsTransient, hooks?.Status, ct).ConfigureAwait(false) is { } changed)
                {
                    return changed;
                }
            }
            else if (verifyHere && !verifiedBeforeClose)
            {
                // Large (or rescued) files: the closed part is read back unbuffered. The wait to reopen is small next to reading the file.
                // Large files take turns to be checked, so two checks never compete for the same drive.
                if (gates is not null)
                {
                    await gates.Check.WaitAsync(ct).ConfigureAwait(false);
                    holdingCheck = true;
                }

                var bad = await VerifyClosedPartAsync(source, part, hash!, options, hooks!, ct).ConfigureAwait(false);
                if (holdingCheck)
                {
                    holdingCheck = false;
                    gates!.Check.Release();
                }

                if (bad is not null)
                {
                    return bad;
                }
            }

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
            if (transfer.Lost.Count > 0)
            {
                var rescueWarning = await new LostReport(_fs, _time).WriteAsync(source, destination, transfer.Lost,
                    hooks?.MustBeUnder ?? Path.GetDirectoryName(originalFinalPath), ct).ConfigureAwait(false);
                warning = warning is null ? rescueWarning : rescueWarning with { Message = rescueWarning.Message + "; " + (warning.Message.StartsWith("The ", StringComparison.Ordinal) ? "the " + warning.Message[4..] : warning.Message) };
            }

            return new FileCopyOutcome(true, hash, warning, part) { Lost = transfer.Lost, Flushed = flushed };
        }
        catch (RescueLimitException)
        {
            return Failed(source, IoErrorKind.BadBlock, CopyMessages.TooMuchUnreadable, part) with { NoRetry = true };
        }
        catch (RetryGaveUpException ex) when (!ct.IsCancellationRequested)
        {
            var atPart = part is not null && SamePath(ex.Last.Path, part);
            var message = CopyMessages.For(ex.Last.Kind, atPart || SamePath(ex.Last.Path, destination), DriveOf(destination));
            return Failed(source, ex.Last.Kind, CopyMessages.RetryGaveUp(message, ex.Waited), part) with { NoRetry = true };
        }
        catch (SourceChangedException)
        {
            return Failed(source, IoErrorKind.Other, CopyMessages.SourceChanged, part);
        }
        catch (PartNotRecordedException)
        {
            return Failed(source, IoErrorKind.InvalidName, CopyMessages.NotRecorded, null); // nothing was created; retrying cannot help
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
            try
            {
                await CleanUpAsync(reader, writer, partIsOurs ? part : null).ConfigureAwait(false);
                hasher?.Dispose();
            }
            finally
            {
                // Every way out gives back a turn it still holds (after the cleanup, so the next file never meets this one's handles).
                if (holdingTransfer)
                {
                    gates!.Transfer.Release();
                }

                if (holdingCheck)
                {
                    gates!.Check.Release();
                }
            }
        }
    }

    private async Task<byte[]> HashRescuedSourceAsync(string source, long length, List<LostRange> lost, HashAlgorithmKind algorithm, CancellationToken ct)
    {
        var buffer = _pool.Rent(DefaultBufferSizeBytes);
        try
        {
            await using var input = _fs.OpenRead(source, unbuffered: false);
            using var hash = new StreamingHasher(algorithm);
            long offset = 0;
            var index = 0;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                var inLost = index < lost.Count && offset >= lost[index].Offset;
                var end = inLost ? lost[index].Offset + lost[index].Length : index < lost.Count ? lost[index].Offset : length;
                var count = (int)Math.Min(buffer.Memory.Length, end - offset);
                if (inLost)
                {
                    buffer.Memory.Span[..count].Clear();
                }
                else
                {
                    var filled = 0;
                    while (filled < count)
                    {
                        var n = await input.ReadAsync(buffer.Memory[filled..count], offset + filled, ct).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            throw new SourceChangedException();
                        }

                        filled += n;
                    }
                }

                hash.Append(buffer.Memory.Span[..count]);
                offset += count;
                if (inLost && offset == end)
                {
                    index++;
                }
            }

            return hash.Finish();
        }
        finally
        {
            _pool.Return(buffer);
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
        Announce(hooks, part);
        return _fs.TryCloneBlocks(source, part) ? part : null;
    }

    /// <summary>The clone already holds the data: hash the source if asked, then the same checks and commit as a normal copy.</summary>
    private async Task<FileCopyOutcome> FinishCloneAsync(
        string source, string destination, CopyOptions options, FileCopyHooks hooks, FileEntry before, string part, IProgress<FileProgress> progress, CancellationToken ct)
    {
        // Accepted risk: a clone is checked by path (the clone call creates the file itself), not through a handle.
        if (hooks.MustBeUnder is { } root && !PathSafety.IsSameOrUnderFinal(root, _fs.GetFinalPath(part)))
        {
            return Failed(source, IoErrorKind.InvalidName, CopyMessages.PartOutsideDestination, part);
        }

        if (hooks.PartCreated is { } created)
        {
            var clone = _fs.GetEntry(part);
            created(part, new FileIdentity(clone.FileId, clone.VolumeSerial));
        }

        byte[]? hash = null;
        if (options.Verify || options.ExportChecksums)
        {
            hash = await Verifier.HashAsync(_fs, source, unbuffered: false, options.HashAlgorithm, ct).ConfigureAwait(false);
        }

        if (CheckComplete(source, before, part, before.Length) is { } incomplete)
        {
            return incomplete;
        }

        if (options.Verify && hooks.VerifyInCopier && await VerifyClosedPartAsync(source, part, hash!, options, hooks, ct).ConfigureAwait(false) is { } bad)
        {
            return bad;
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

    /// <summary>Reads a closed part file back unbuffered and compares it with the source hash; null when it matches.</summary>
    private async Task<FileCopyOutcome?> VerifyClosedPartAsync(
        string source, string part, byte[] hash, CopyOptions options, FileCopyHooks hooks, CancellationToken ct)
    {
        hooks.Verifying?.Invoke();
        try
        {
            return await Verifier.MatchesAsync(_fs, part, hash, options.HashAlgorithm, ct).ConfigureAwait(false)
                ? null
                : Failed(source, IoErrorKind.Other, CopyMessages.VerifyMismatch, part) with { VerifyFailed = true };
        }
        catch (FileSystemException ex) when (!ct.IsCancellationRequested)
        {
            return Failed(source, ex.Kind, CopyMessages.VerifyCouldNotRead, part) with { VerifyFailed = true };
        }
    }

    /// <summary>True when the job said which file it listed and <paramref name="entry"/> is another one (ids of zero are not compared).</summary>
    private static bool IsNotTheListedFile(FileCopyHooks? hooks, FileEntry entry) =>
        hooks?.ExpectedSource is { } expected && expected.FileId != UInt128.Zero
        && (entry.FileId != expected.FileId || entry.VolumeSerial != expected.VolumeSerial);

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

    /// <summary>Hashes the first <paramref name="length"/> bytes read back through the still-open part handle and compares with the source hash.</summary>
    private async Task<bool> ReadBackMatchesAsync(IWriteHandle writer, long length, int alignment, byte[] expected, HashAlgorithmKind algorithm, CancellationToken ct)
    {
        const int Chunk = 1024 * 1024;
        var buffer = _pool.Rent(Chunk);
        try
        {
            using var hasher = new StreamingHasher(algorithm);
            long offset = 0;
            while (offset < length)
            {
                var ask = (int)Math.Min(Chunk, AlignUp(length - offset, alignment));
                var n = await writer.ReadBackAsync(buffer.Memory[..ask], offset, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    return false; // shorter than what was written
                }

                var take = (int)Math.Min(n, length - offset);
                hasher.Append(buffer.Memory.Span[..take]);
                offset += take;
            }

            var actual = hasher.Finish();
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            _pool.Return(buffer);
        }
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

    /// <summary>Tells the caller the part name before the file exists; a refusal stops the copy before anything is created.</summary>
    private static void Announce(FileCopyHooks? hooks, string part)
    {
        if (hooks?.PartChosen is { } chosen && !chosen(part))
        {
            throw new PartNotRecordedException();
        }
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
            Announce(hooks, part); // the job journals the name before the file exists
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

    /// <summary>A read in flight: its buffer, where it reads, and the read itself (awaited exactly once).</summary>
    private record struct PendingRead(AlignedBuffer Buffer, long Offset, int Count, ValueTask Task);

    /// <summary>The caller refused a part-file name (it could not journal it), so no part file may be created.</summary>
    private sealed class PartNotRecordedException : Exception
    {
        public PartNotRecordedException()
            : base(CopyMessages.NotRecorded)
        {
        }
    }

    /// <summary>State of one copy's data transfer. Reads and writes run several at once; shared state is interlocked or under a lock.</summary>
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
        bool unbuffered,
        bool writeUnbuffered,
        RescueReader? rescue,
        int pipelineDepth)
    {
        public List<LostRange> Lost { get; } = []; // read side only (rescue mode reads one chunk at a time)
        private readonly Lock _progressLock = new();
        private Exception? _firstFailure; // whichever read or write faulted first
        private long _done; // bytes written; several writes add to it as they finish
        private long _lastReport = time.GetTimestamp(); // under _progressLock

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
        /// <remarks>Reports are made one at a time and read the running total inside the lock, so the value reported never goes down.</remarks>
        public void ReportProgress(bool force)
        {
            lock (_progressLock)
            {
                var now = time.GetTimestamp();
                if (!force && time.GetElapsedTime(_lastReport, now) < ProgressInterval)
                {
                    return;
                }

                _lastReport = now;
                progress.Report(new FileProgress(name, Interlocked.Read(ref _done), length));
            }
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
                hasher?.Append(buffer.Memory.Span[..count]);
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

        /// <summary>
        /// Keeps up to <c>pipelineDepth</c> reads and as many writes in flight (rescue mode: one read at a time, its bisection assumes it).
        /// Reads are issued at increasing offsets and taken back in issue order, so the hash sees the file in order; the speed limit waits
        /// per chunk before its write starts. Writes may finish in any order (each chunk has its own offset) and give their buffer back
        /// themselves. At most 2 × depth buffers are rented at once. Every read and write has ended before this returns, whatever happens.
        /// </summary>
        /// <remarks>
        /// Each read and write starts on a thread-pool thread: Windows runs a write that extends the part file's valid data (every write of a
        /// fresh part does) inside the call itself, so issuing them one after another from one thread would run them one at a time.
        /// Rules from ruling R27: (1) the final short (unaligned) tail write is issued only after every earlier write has completed, because
        /// the handle zero-pads it to a whole sector and trims the file to the data end on flush; (2) progress adds bytes as each write completes
        /// and never goes down. Part handles are opened without write-through (it slowed copies, task 16): FlushFileBuffers runs before the
        /// rename, so the real name only appears on durable data (small files may skip it; the journal then marks them for a recheck on resume).
        /// </remarks>
        private async Task RunPipelineAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = cts.Token;
            var readDepth = rescue is null ? pipelineDepth : 1;
            var reads = new PendingRead[readDepth];
            var writes = new ValueTask[pipelineDepth];
            int readHead = 0, readCount = 0, writeHead = 0, writeCount = 0;
            AlignedBuffer? inHand = null;
            Exception? failure = null;
            try
            {
                long nextRead = 0;
                for (long nextWrite = 0; nextWrite < length;)
                {
                    while (readCount < readDepth && nextRead < length)
                    {
                        await pause.WaitIfPausedAsync(token).ConfigureAwait(false); // no new chunk starts while paused
                        var count = (int)Math.Min(chunkSize, length - nextRead);
                        var buffer = pool.Rent(chunkSize);
                        var slot = (readHead + readCount) % readDepth;
                        reads[slot] = new PendingRead(buffer, nextRead, count, default);
                        readCount++; // counted before it starts, so a failure still gives the buffer back
#pragma warning disable CA2012 // Kept in a ring and awaited exactly once, in issue order (or drained once on failure).
                        reads[slot].Task = ReadInFlightAsync(buffer, nextRead, count, cts);
#pragma warning restore CA2012
                        nextRead += count;
                    }

                    var read = reads[readHead];
                    reads[readHead] = default;
                    readHead = (readHead + 1) % readDepth;
                    readCount--;
                    inHand = read.Buffer;
                    await read.Task.ConfigureAwait(false);
                    hasher?.Append(read.Buffer.Memory.Span[..read.Count]);
                    if (limiter is not null)
                    {
                        await limiter.WaitAsync(read.Count, token).ConfigureAwait(false);
                    }

                    // A short unaligned tail is padded by the handle, so it goes last, alone.
                    var tail = writeUnbuffered && read.Count % alignment != 0;
                    while (writeCount > 0 && (writeCount == writes.Length || tail))
                    {
                        var oldest = writes[writeHead];
                        writes[writeHead] = default;
                        writeHead = (writeHead + 1) % writes.Length;
                        writeCount--;
                        await oldest.ConfigureAwait(false);
                    }

#pragma warning disable CA2012 // Kept in a ring and awaited exactly once, in issue order (or drained once on failure).
                    writes[(writeHead + writeCount) % writes.Length] = WriteInFlightAsync(new Chunk(read.Buffer, read.Offset, read.Count), cts);
#pragma warning restore CA2012
                    writeCount++;
                    inHand = null; // the write gives it back
                    nextWrite += read.Count;
                }

                while (writeCount > 0)
                {
                    var oldest = writes[writeHead];
                    writes[writeHead] = default;
                    writeHead = (writeHead + 1) % writes.Length;
                    writeCount--;
                    await oldest.ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex)
            {
                failure = ex;
                RecordFailure(ex);
                await cts.CancelAsync().ConfigureAwait(false);
            }

            // Stopped early: wait for every read and write still running before any buffer goes back or a handle closes.
            Exception? drained = null;
            for (; readCount > 0; readCount--, readHead = (readHead + 1) % readDepth)
            {
                drained = await DrainAsync(reads[readHead].Task).ConfigureAwait(false) ?? drained;
                pool.Return(reads[readHead].Buffer);
            }

            for (; writeCount > 0; writeCount--, writeHead = (writeHead + 1) % writes.Length)
            {
                drained = await DrainAsync(writes[writeHead]).ConfigureAwait(false) ?? drained;
            }

            if (inHand is not null)
            {
                pool.Return(inHand);
            }

            // Report the failure that started it, not the cancellation (or aborted I/O) it then caused elsewhere.
            var first = Volatile.Read(ref _firstFailure);
            var primary = IsReal(first) ? first : IsReal(failure) ? failure : drained;
            if (primary is not null)
            {
                ExceptionDispatchInfo.Throw(primary);
            }

            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException(ct);
        }

        /// <summary>Waits for an I/O that is being abandoned; returns its failure when it is a real one.</summary>
        private static async ValueTask<Exception?> DrainAsync(ValueTask pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return IsReal(ex) ? ex : null;
            }
        }

        private static bool IsReal(Exception? e) => e is not null and not OperationCanceledException;

        private void RecordFailure(Exception ex) => Interlocked.CompareExchange(ref _firstFailure, ex, null);

        /// <summary>One chunk's read, started on the thread pool; a failure stops the whole transfer.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask ReadInFlightAsync(AlignedBuffer buffer, long offset, int count, CancellationTokenSource cts)
        {
            try
            {
                if (ThreadPoolHop.YieldReachesPool)
                {
                    await Task.Yield();
                }
                else
                {
                    await default(ThreadPoolHop);
                }

                await ReadChunkAsync(buffer, offset, count, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>One chunk's write, started on the thread pool; gives the chunk's buffer back when it ends, however it ends.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask WriteInFlightAsync(Chunk chunk, CancellationTokenSource cts)
        {
            try
            {
                if (ThreadPoolHop.YieldReachesPool)
                {
                    await Task.Yield();
                }
                else
                {
                    await default(ThreadPoolHop);
                }

                await WriteChunkAsync(chunk, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                await cts.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                pool.Return(chunk.Buffer);
            }
        }

        /// <summary>Fills <paramref name="count"/> bytes (reads may come back short, so it loops). The caller hashes them, in file order.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask ReadChunkAsync(AlignedBuffer buffer, long offset, int count, CancellationToken ct)
        {
            // Unbuffered reads ask for whole sectors (the end of file just returns fewer bytes).
            var memory = buffer.Memory[..(unbuffered ? (int)AlignUp(count, alignment) : count)];
            var filled = 0;
            try
            {
                while (filled < count)
                {
                    var n = await reader.ReadAsync(memory[filled..], offset + filled, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        throw new SourceChangedException(); // shorter than when the copy started
                    }

                    filled += n;
                }
            }
            catch (FileSystemException ex) when (ex.Kind == IoErrorKind.BadBlock && rescue is not null)
            {
                foreach (var range in await rescue.ReadChunkAsync(reader, memory, offset, ct).ConfigureAwait(false))
                {
                    if (Lost.Count > 0 && Lost[^1] is var last && last.Offset + last.Length == range.Offset && (long)last.Length + range.Length <= int.MaxValue)
                    {
                        Lost[^1] = last with { Length = last.Length + range.Length };
                    }
                    else
                    {
                        Lost.Add(range);
                    }
                }
            }

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
            var whole = writeUnbuffered ? chunk.Length - (chunk.Length % alignment) : chunk.Length;
            if (whole > 0)
            {
                await writer.WriteAsync(data[..whole], chunk.Offset, ct).ConfigureAwait(false);
            }

            if (whole < chunk.Length)
            {
                await writer.WriteAsync(data[whole..], chunk.Offset + whole, ct).ConfigureAwait(false);
            }

            Interlocked.Add(ref _done, chunk.Length);
            ReportProgress(force: pause.IsPaused); // while paused, show every write that still finishes
        }
    }
}
