using System.Collections.Concurrent;
using System.Numerics;

namespace EveryApp.Core.FileSystem;

/// <summary>Thread-safe pool of <see cref="AlignedBuffer"/>s, sized in powers of two. Native memory is freed when the pool is disposed.</summary>
public sealed class AlignedBufferPool : IDisposable
{
    private const int MinSize = 4096;
    private const int MaxSize = 1 << 30;
    private const int MaxPerBucket = 16;
    private readonly ConcurrentQueue<AlignedBuffer>[] _buckets = new ConcurrentQueue<AlignedBuffer>[31];
    private long _rentCount;
    private volatile bool _disposed;

    public AlignedBufferPool()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new ConcurrentQueue<AlignedBuffer>();
        }
    }

    /// <summary>Process-wide pool used when no pool is supplied. Never disposed.</summary>
    public static AlignedBufferPool Shared { get; } = new();

    /// <summary>Number of <see cref="Rent"/> calls so far (lets tests prove a code path did not allocate buffers).</summary>
    public long RentCount => Interlocked.Read(ref _rentCount);

    /// <summary>Returns a buffer of at least <paramref name="minSize"/> bytes. Contents are not cleared.</summary>
    public AlignedBuffer Rent(int minSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minSize, MaxSize);
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minSize, MinSize));
        Interlocked.Increment(ref _rentCount);
        return _buckets[BitOperations.Log2((uint)size)].TryDequeue(out var buffer) ? buffer : new AlignedBuffer(size);
    }

    /// <summary>Gives a buffer back. The caller must not use it afterwards.</summary>
    public void Return(AlignedBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var queue = _buckets[BitOperations.Log2((uint)buffer.Size)];
        if (_disposed || queue.Count >= MaxPerBucket)
        {
            ((IDisposable)buffer).Dispose();
            return;
        }

        queue.Enqueue(buffer);
        if (_disposed)
        {
            Drain();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Drain();
    }

    private void Drain()
    {
        foreach (var q in _buckets)
        {
            while (q.TryDequeue(out var b))
            {
                ((IDisposable)b).Dispose();
            }
        }
    }
}
