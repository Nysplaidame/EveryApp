using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Hashing;

/// <summary>
/// Cheap pre-filter for same-size files: hashes the length, the first and the last <c>headTailBytes</c>.
/// Equal partial hashes do not prove equal files (compare with a full hash); different ones prove different files.
/// </summary>
public static class PartialHash
{
    private const int MaxHeadTailBytes = 64 * 1024 * 1024;

    public static async ValueTask<ulong> ComputeAsync(IFileSystem fs, string path, int headTailBytes = 65536, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentOutOfRangeException.ThrowIfLessThan(headTailBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(headTailBytes, MaxHeadTailBytes);
        ct.ThrowIfCancellationRequested();

        await using var handle = fs.OpenRead(path, unbuffered: false);
        var length = handle.Length;
        var whole = length <= 2L * headTailBytes;
        var headLen = whole ? (int)length : headTailBytes;
        var tailLen = whole ? 0 : headTailBytes;

        var hasher = new XxHash3();
        Span<byte> lengthBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hasher.Append(lengthBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(headLen + tailLen, 1));
        try
        {
            var got = await ReadFullyAsync(handle, buffer.AsMemory(0, headLen), 0, ct).ConfigureAwait(false);
            hasher.Append(buffer.AsSpan(0, got));
            if (tailLen > 0)
            {
                got = await ReadFullyAsync(handle, buffer.AsMemory(0, tailLen), length - tailLen, ct).ConfigureAwait(false);
                hasher.Append(buffer.AsSpan(0, got));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt64();
    }

    /// <summary>Reads until the buffer is full or the file ends (a file that shrank meanwhile yields fewer bytes).</summary>
    private static async ValueTask<int> ReadFullyAsync(IReadHandle handle, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await handle.ReadAsync(buffer[total..], offset + total, ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
