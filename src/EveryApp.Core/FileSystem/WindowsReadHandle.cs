using Microsoft.Win32.SafeHandles;

namespace EveryApp.Core.FileSystem;

internal sealed class WindowsReadHandle : IReadHandle
{
    private const int ChunkSize = 1024 * 1024;
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly int _sector; // 0 = buffered
    private readonly AlignedBuffer? _aligned;
    private readonly SemaphoreSlim? _gate;

    public WindowsReadHandle(SafeFileHandle handle, string path, int sectorSize)
    {
        _handle = handle;
        _path = path;
        _sector = sectorSize;
        Length = RandomAccess.GetLength(handle);
        if (sectorSize > 0)
        {
            _aligned = new AlignedBuffer(ChunkSize);
            _gate = new SemaphoreSlim(1, 1);
        }
    }

    public long Length { get; }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return 0;
        }

        try
        {
            if (_aligned is null)
            {
                return await RandomAccess.ReadAsync(_handle, buffer, offset, ct).ConfigureAwait(false);
            }

            // Unbuffered: offset, length and buffer address must be sector aligned, so read a whole-sector window and slice it.
            await _gate!.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var alignedStart = offset - (offset % _sector);
                var lead = (int)(offset - alignedStart);
                var want = Math.Min(buffer.Length, ChunkSize - lead);
                var readLen = (int)((((long)lead + want + _sector) - 1) / _sector * _sector);
                var read = await RandomAccess.ReadAsync(_handle, _aligned.Memory[..readLen], alignedStart, ct).ConfigureAwait(false);
                var usable = Math.Min(want, read - lead);
                if (usable <= 0)
                {
                    return 0;
                }

                _aligned.Memory.Slice(lead, usable).CopyTo(buffer);
                return usable;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        ((IDisposable?)_aligned)?.Dispose();
        _gate?.Dispose();
        return ValueTask.CompletedTask;
    }
}
