using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;

namespace Ferry.Engine;

/// <summary>Reads a file back and compares it with the hash taken while it was copied.</summary>
public static class Verifier
{
    private const int ChunkSize = FileCopier.DefaultBufferSizeBytes;
    private const int Depth = FileCopier.PipelineDepth;

    /// <summary>
    /// True when <paramref name="destination"/> hashes to <paramref name="expectedHash"/>. The file is read UNBUFFERED, so the data comes from
    /// the disk and a stale page in the cache cannot fake a match. Read failures throw <see cref="FileSystemException"/>.
    /// </summary>
    public static async Task<bool> MatchesAsync(IFileSystem fs, string destination, byte[] expectedHash, HashAlgorithmKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(expectedHash);
        var actual = await HashAsync(fs, destination, unbuffered: true, kind, ct).ConfigureAwait(false);
        return actual.Length == expectedHash.Length && CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    /// <summary>
    /// Hashes a whole file. A file that ends early just hashes what is there (so it will not match). Up to
    /// <see cref="FileCopier.PipelineDepth"/> reads of <see cref="FileCopier.DefaultBufferSizeBytes"/> are in flight at once; they are hashed
    /// in file order.
    /// </summary>
    public static Task<byte[]> HashAsync(IFileSystem fs, string path, bool unbuffered, HashAlgorithmKind kind, CancellationToken ct) =>
        HashAsync(fs, path, unbuffered, kind, BufferPool.Shared, ct);

    /// <summary>As the public overload, renting its buffers from <paramref name="pool"/> (lets tests count them).</summary>
    internal static async Task<byte[]> HashAsync(IFileSystem fs, string path, bool unbuffered, HashAlgorithmKind kind, BufferPool pool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(pool);
        var reader = fs.OpenRead(path, unbuffered);
        await using (reader.ConfigureAwait(false))
        {
            using var hasher = new StreamingHasher(kind);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var length = reader.Length;
            var reads = new (AlignedBuffer Buffer, int Want, ValueTask<int> Task)[Depth];
            int head = 0, count = 0;
            try
            {
                long next = 0;
                var ended = false;
                while (!ended && (count > 0 || next < length))
                {
                    while (count < Depth && next < length)
                    {
                        var want = (int)Math.Min(ChunkSize, length - next);
                        var buffer = pool.Rent(ChunkSize);
                        var slot = (head + count) % Depth;
                        reads[slot] = (buffer, want, default);
                        count++; // counted before it starts, so the buffer always goes back
#pragma warning disable CA2012 // Kept in a ring and awaited exactly once, in issue order (or drained once on failure).
                        reads[slot].Task = FillAsync(reader, buffer, next, want, cts.Token);
#pragma warning restore CA2012
                        next += want;
                    }

                    var (held, expected, pending) = reads[head];
                    reads[head] = default;
                    head = (head + 1) % Depth;
                    count--;
                    try
                    {
                        var filled = await pending.ConfigureAwait(false);
                        hasher.Append(held.Memory.Span[..filled]);
                        ended = filled < expected; // the file ended early: what follows is not hashed
                    }
                    finally
                    {
                        pool.Return(held);
                    }
                }

                return hasher.Finish();
            }
            finally
            {
                // Stopped early (failure, cancel or a short file): wait for every read still running before its buffer goes back.
                if (count > 0)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    for (; count > 0; count--, head = (head + 1) % Depth)
                    {
#pragma warning disable CA1031 // Abandoned reads: the failure that stopped the hash (if any) is the one reported.
                        try
                        {
                            await reads[head].Task.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                        }
#pragma warning restore CA1031

                        pool.Return(reads[head].Buffer);
                    }
                }
            }
        }
    }

    /// <summary>Reads up to <paramref name="want"/> bytes at <paramref name="offset"/> on the thread pool; fewer only when the file ends.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<int> FillAsync(IReadHandle reader, AlignedBuffer buffer, long offset, int want, CancellationToken ct)
    {
        if (ThreadPoolHop.YieldReachesPool)
        {
            await Task.Yield();
        }
        else
        {
            await default(ThreadPoolHop);
        }

        // The whole chunk is asked for, so unbuffered reads stay whole sectors (the end of the file just returns fewer bytes).
        var memory = buffer.Memory[..ChunkSize];
        var filled = 0;
        while (filled < want)
        {
            var n = await reader.ReadAsync(memory[filled..], offset + filled, ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            filled += n;
        }

        return Math.Min(filled, want);
    }
}
