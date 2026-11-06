using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EveryApp.Core.FileSystem;

internal sealed class WindowsReadHandle : IReadHandle
{
    private const int BounceSize = 1024 * 1024;
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly int _sector; // 0 = buffered
    private readonly AlignedBufferPool _pool;
    private readonly IoGate _gate = new(nameof(WindowsReadHandle));
    private readonly SemaphoreSlim _bounceLock = new(1, 1);
    private AlignedBuffer? _bounce;

    public WindowsReadHandle(SafeFileHandle handle, string path, int sectorSize, AlignedBufferPool pool)
    {
        _handle = handle;
        _path = path;
        _sector = sectorSize;
        _pool = pool;
        Length = RandomAccess.GetLength(handle);
    }

    public long Length { get; }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return 0;
        }

        await _gate.EnterSharedAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sector == 0 || IsDirect(buffer, offset))
            {
                return await RandomAccess.ReadAsync(_handle, buffer, offset, ct).ConfigureAwait(false);
            }

            return await ReadBouncedAsync(buffer, offset, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
        finally
        {
            _gate.ExitShared();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await _gate.BeginDisposeAsync().ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _handle.Dispose();
            if (_bounce is not null)
            {
                _pool.Return(_bounce);
                _bounce = null;
            }
        }
        finally
        {
            _gate.EndDispose();
            _gate.Dispose();
        }
    }

    /// <summary>True when pointer, offset and length are all sector aligned, so the kernel can fill the caller's memory directly.</summary>
    private bool IsDirect(Memory<byte> buffer, long offset) =>
        offset % _sector == 0
        && buffer.Length % _sector == 0
        && MemoryMarshal.TryGetMemoryManager<byte, AlignedBuffer>(buffer, out _, out var start, out _)
        && start % _sector == 0
        && _sector <= AlignedBuffer.Alignment;

    // Unbuffered with unaligned input: read a whole-sector window into a bounce buffer and slice it. Only this path is serialised.
    private async ValueTask<int> ReadBouncedAsync(Memory<byte> buffer, long offset, CancellationToken ct)
    {
        await _bounceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _bounce ??= _pool.Rent(BounceSize);
            var alignedStart = offset - (offset % _sector);
            var lead = (int)(offset - alignedStart);
            var want = Math.Min(buffer.Length, _bounce.Size - lead);
            var readLen = (int)((((long)lead + want + _sector) - 1) / _sector * _sector);
            var read = await RandomAccess.ReadAsync(_handle, _bounce.Memory[..readLen], alignedStart, ct).ConfigureAwait(false);
            var usable = Math.Min(want, read - lead);
            if (usable <= 0)
            {
                return 0;
            }

            _bounce.Memory.Slice(lead, usable).CopyTo(buffer);
            return usable;
        }
        finally
        {
            _bounceLock.Release();
        }
    }
}
