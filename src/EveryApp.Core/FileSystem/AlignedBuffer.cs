using System.Buffers;
using System.Runtime.InteropServices;

namespace EveryApp.Core.FileSystem;

/// <summary>
/// Native buffer aligned to 64 KiB (covers every sector size), required for zero-copy unbuffered (FILE_FLAG_NO_BUFFERING) I/O.
/// Get one from <see cref="AlignedBufferPool.Rent"/>; slice <see cref="MemoryManager{T}.Memory"/> on sector multiples to do direct I/O.
/// </summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    internal const int Alignment = 65536;
    private nint _ptr;
    private int _inPool;

    internal AlignedBuffer(int length)
    {
        Size = length;
        _ptr = (nint)NativeMemory.AlignedAlloc((nuint)length, Alignment);
    }

    /// <summary>Set while the buffer sits in a pool; used to detect double returns.</summary>
    internal bool TryMarkReturned() => Interlocked.Exchange(ref _inPool, 1) == 0;

    internal void MarkRented() => Volatile.Write(ref _inPool, 0);

    /// <summary>Usable length in bytes (a power of two, at least 4096).</summary>
    public int Size { get; }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_ptr == 0, this);
        return new Span<byte>((void*)_ptr, Size);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_ptr == 0, this);
        return new MemoryHandle((byte*)_ptr + elementIndex);
    }

    public override void Unpin()
    {
        // Native memory never moves.
    }

    protected override void Dispose(bool disposing)
    {
        var p = Interlocked.Exchange(ref _ptr, 0);
        if (p != 0)
        {
            NativeMemory.AlignedFree((void*)p);
        }
    }
}
