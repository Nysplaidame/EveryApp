using System.IO.Hashing;
using System.Security.Cryptography;

namespace EveryApp.Core.Hashing;

public enum HashAlgorithmKind
{
    /// <summary>Fast 64-bit non-cryptographic hash (seed 0). Result is 8 bytes, big-endian (canonical form).</summary>
    XxHash3,

    Sha256,

    Md5,
}

/// <summary>
/// Incremental hasher. Append never allocates. Finish may be called once; after it, Append and Finish throw
/// <see cref="InvalidOperationException"/>. Not thread safe.
/// </summary>
public sealed class StreamingHasher : IDisposable
{
    private readonly XxHash3? _xx;
    private readonly IncrementalHash? _inc;
    private bool _finished;
    private bool _disposed;

    public StreamingHasher(HashAlgorithmKind kind)
    {
        switch (kind)
        {
            case HashAlgorithmKind.XxHash3:
                _xx = new XxHash3();
                break;
            case HashAlgorithmKind.Sha256:
                _inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                break;
            case HashAlgorithmKind.Md5:
                _inc = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown hash algorithm.");
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        ThrowIfUnusable();
        if (_xx is not null)
        {
            _xx.Append(data);
        }
        else
        {
            _inc!.AppendData(data);
        }
    }

    public byte[] Finish()
    {
        ThrowIfUnusable();
        _finished = true;
        return _xx is not null ? _xx.GetCurrentHash() : _inc!.GetHashAndReset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inc?.Dispose();
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
        {
            throw new InvalidOperationException("The hash has already been finished.");
        }
    }
}
