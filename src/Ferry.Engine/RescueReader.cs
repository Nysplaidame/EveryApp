using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>A stretch of a file that could not be read and was filled with blanks (zeros) in the copy.</summary>
public sealed record LostRange(long Offset, int Length);

/// <summary>
/// Rescue mode for failing drives: reads a chunk that failed with a bad block piece by piece, so every readable byte is saved. The chunk is
/// split in halves down to single sectors; a sector that still fails is tried twice more, then filled with zeros and reported as lost.
/// One instance serves one file: it counts the sectors lost and gives up (<see cref="RescueLimitException"/>) past
/// <see cref="MaxLostSectors"/>, so a dead drive cannot take hours per file. Only bad blocks are rescued; any other failure passes through.
/// </summary>
public sealed class RescueReader
{
    /// <summary>Default for the most sectors one file may lose before rescue gives up on it.</summary>
    public const int DefaultMaxLostSectors = 4096;

    /// <summary>Extra tries for a single failing sector, after the first.</summary>
    public const int SectorRetries = 2;

    private readonly int _sectorSize;
    private readonly PauseGate? _pause;
    private int _lostSectors;

    /// <param name="fs">The file system the handles come from (sector size and handles must belong to it).</param>
    /// <param name="sectorSize">Smallest unit to read: the source volume's sector size (a power of two).</param>
    /// <param name="pause">Rescue waits here before each read while the job is paused.</param>
    /// <param name="maxLostSectors">Most sectors this file may lose before rescue gives up on it.</param>
    public RescueReader(IFileSystem fs, int sectorSize, PauseGate? pause = null, int maxLostSectors = DefaultMaxLostSectors)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectorSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLostSectors);
        _sectorSize = sectorSize;
        _pause = pause;
        MaxLostSectors = maxLostSectors;
    }

    public int MaxLostSectors { get; }

    /// <summary>Sectors lost so far in this file.</summary>
    public int LostSectors => _lostSectors;

    /// <summary>
    /// Fills <paramref name="buffer"/> with the file's data from <paramref name="offset"/> (up to the end of the file), zero-filling whatever
    /// cannot be read. Returns the lost ranges, merged and in order (empty when everything was read). <paramref name="offset"/> must be a
    /// multiple of the sector size; on an unbuffered handle the buffer must be aligned and a whole number of sectors long.
    /// </summary>
    public async Task<IReadOnlyList<LostRange>> ReadChunkAsync(IReadHandle h, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var count = (int)Math.Min(buffer.Length, Math.Max(0, h.Length - offset));
        var lost = new List<LostRange>();
        if (count > 0)
        {
            await ReadRangeAsync(h, buffer, offset, 0, count, lost, ct).ConfigureAwait(false);
        }

        return Merge(lost);
    }

    /// <summary>Reads <paramref name="length"/> data bytes at <paramref name="start"/> in the buffer, splitting on bad blocks.</summary>
    private async Task ReadRangeAsync(IReadHandle h, Memory<byte> buffer, long fileOffset, int start, int length, List<LostRange> lost, CancellationToken ct)
    {
        if (await TryFillAsync(h, buffer, fileOffset, start, length, ct).ConfigureAwait(false))
        {
            return;
        }

        if (length > _sectorSize)
        {
            var half = AlignUp(length / 2); // always below length: length > one sector
            await ReadRangeAsync(h, buffer, fileOffset, start, half, lost, ct).ConfigureAwait(false);
            await ReadRangeAsync(h, buffer, fileOffset, start + half, length - half, lost, ct).ConfigureAwait(false);
            return;
        }

        for (var retry = 0; retry < SectorRetries; retry++)
        {
            if (await TryFillAsync(h, buffer, fileOffset, start, length, ct).ConfigureAwait(false))
            {
                return;
            }
        }

        buffer.Span.Slice(start, length).Clear();
        lost.Add(new LostRange(fileOffset + start, length));
        if (++_lostSectors > MaxLostSectors)
        {
            throw new RescueLimitException();
        }
    }

    /// <summary>One attempt at a range; false on a bad block. Reads whole sectors (the end of the file just returns fewer bytes).</summary>
    private async Task<bool> TryFillAsync(IReadHandle h, Memory<byte> buffer, long fileOffset, int start, int length, CancellationToken ct)
    {
        if (_pause is not null)
        {
            await _pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var span = buffer.Slice(start, Math.Min(AlignUp(length), buffer.Length - start));
        var filled = 0;
        try
        {
            while (filled < length)
            {
                var n = await h.ReadAsync(span[filled..], fileOffset + start + filled, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new SourceChangedException(); // shorter than when the copy started
                }

                filled += n;
            }

            return true;
        }
        catch (FileSystemException ex) when (ex.Kind == IoErrorKind.BadBlock)
        {
            return false;
        }
    }

    private int AlignUp(int value) => (int)((((long)value + _sectorSize - 1) / _sectorSize) * _sectorSize);

    private static List<LostRange> Merge(List<LostRange> ranges)
    {
        var merged = new List<LostRange>(ranges.Count);
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Offset + last.Length == r.Offset)
            {
                merged[^1] = last with { Length = last.Length + r.Length };
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }
}

/// <summary>Too much of a file was unreadable: rescue gave up on it.</summary>
internal sealed class RescueLimitException : Exception
{
    public RescueLimitException()
        : base(CopyMessages.TooMuchUnreadable)
    {
    }
}

/// <summary>The source ended early or grew while it was read.</summary>
internal sealed class SourceChangedException : Exception
{
    public SourceChangedException()
        : base(CopyMessages.SourceChanged)
    {
    }
}
