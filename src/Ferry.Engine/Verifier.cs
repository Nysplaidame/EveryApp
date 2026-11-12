using System.Security.Cryptography;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;

namespace Ferry.Engine;

/// <summary>Reads a file back and compares it with the hash taken while it was copied.</summary>
public static class Verifier
{
    private const int ChunkSize = 1024 * 1024;

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

    /// <summary>Hashes a whole file. A file that ends early just hashes what is there (so it will not match).</summary>
    internal static async Task<byte[]> HashAsync(IFileSystem fs, string path, bool unbuffered, HashAlgorithmKind kind, CancellationToken ct)
    {
        var buffer = BufferPool.Shared.Rent(ChunkSize);
        try
        {
            var reader = fs.OpenRead(path, unbuffered);
            await using (reader.ConfigureAwait(false))
            {
                using var hasher = new StreamingHasher(kind);
                var length = reader.Length;
                long offset = 0;
                while (offset < length)
                {
                    var n = await reader.ReadAsync(buffer.Memory[..ChunkSize], offset, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    var take = (int)Math.Min(n, length - offset);
                    hasher.Append(buffer.Memory.Span[..take]);
                    offset += take;
                }

                return hasher.Finish();
            }
        }
        finally
        {
            BufferPool.Shared.Return(buffer);
        }
    }
}
