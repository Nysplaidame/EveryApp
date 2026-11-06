using System.Buffers;
using System.Runtime.InteropServices;

namespace EveryApp.Core.FileSystem;

/// <summary>Page-aligned native buffer, required for unbuffered (FILE_FLAG_NO_BUFFERING) I/O.</summary>
internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private const int Alignment = 4096;
    private void* _ptr;
    private readonly int _length;

    public AlignedBuffer(int length)
    {
        _length = length;
        _ptr = NativeMemory.AlignedAlloc((nuint)length, Alignment);
    }

    public override Span<byte> GetSpan() => new(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_ptr + elementIndex);

    public override void Unpin()
    {
        // Native memory never moves.
    }

    protected override void Dispose(bool disposing)
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}
