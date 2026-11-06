using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>
/// Ferry's copy buffers: a thin counting wrapper over <see cref="AlignedBufferPool"/> (sector-aligned native memory, so unbuffered I/O
/// goes straight to the OS with no copy). The counters let tests prove that no code path leaks a rented buffer. Thread safe.
/// </summary>
public sealed class BufferPool : IDisposable
{
    private readonly AlignedBufferPool _inner;
    private readonly bool _ownsInner;
    private long _rented;
    private long _returned;

    /// <summary>Creates a pool with its own native memory, freed on <see cref="Dispose"/>.</summary>
    public BufferPool()
        : this(new AlignedBufferPool(), ownsInner: true)
    {
    }

    /// <summary>Wraps an existing pool (not disposed by this wrapper).</summary>
    public BufferPool(AlignedBufferPool inner)
        : this(inner, ownsInner: false)
    {
    }

    private BufferPool(AlignedBufferPool inner, bool ownsInner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
    }

    /// <summary>Process-wide pool over <see cref="AlignedBufferPool.Shared"/>.</summary>
    public static BufferPool Shared { get; } = new(AlignedBufferPool.Shared);

    public long RentCount => Interlocked.Read(ref _rented);

    public long ReturnCount => Interlocked.Read(ref _returned);

    /// <summary>Buffers rented and not yet returned.</summary>
    public long Outstanding => RentCount - ReturnCount;

    /// <summary>Returns a buffer of at least <paramref name="minSize"/> bytes, aligned for unbuffered I/O. Contents are not cleared.</summary>
    public AlignedBuffer Rent(int minSize)
    {
        var buffer = _inner.Rent(minSize);
        Interlocked.Increment(ref _rented);
        return buffer;
    }

    public void Return(AlignedBuffer buffer)
    {
        _inner.Return(buffer);
        Interlocked.Increment(ref _returned);
    }

    public void Dispose()
    {
        if (_ownsInner)
        {
            _inner.Dispose();
        }
    }
}
