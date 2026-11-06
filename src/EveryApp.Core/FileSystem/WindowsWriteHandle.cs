using Microsoft.Win32.SafeHandles;

namespace EveryApp.Core.FileSystem;

internal sealed class WindowsWriteHandle : IWriteHandle
{
    private const int ChunkSize = 1024 * 1024;
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly int _sector; // 0 = buffered
    private readonly AlignedBuffer? _aligned;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _logicalLength;
    private bool _padded;

    public WindowsWriteHandle(SafeFileHandle handle, string path, int sectorSize, long initialLength)
    {
        _handle = handle;
        _path = path;
        _sector = sectorSize;
        _logicalLength = initialLength;
        if (sectorSize > 0)
        {
            _aligned = new AlignedBuffer(ChunkSize);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return;
        }

        if (_aligned is not null && offset % _sector != 0)
        {
            throw new ArgumentException("Unbuffered write offset must be sector aligned.", nameof(offset));
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_aligned is null)
                {
                    await RandomAccess.WriteAsync(_handle, buffer, offset, ct).ConfigureAwait(false);
                }
                else
                {
                    var done = 0;
                    while (done < buffer.Length)
                    {
                        var n = Math.Min(ChunkSize, buffer.Length - done);
                        var padded = (int)((((long)n + _sector) - 1) / _sector * _sector);
                        var span = _aligned.Memory.Span;
                        buffer.Span.Slice(done, n).CopyTo(span);
                        if (padded > n)
                        {
                            span[n..padded].Clear();
                            _padded = true;
                        }

                        await RandomAccess.WriteAsync(_handle, _aligned.Memory[..padded], offset + done, ct).ConfigureAwait(false);
                        done += n;
                    }
                }

                _logicalLength = Math.Max(_logicalLength, offset + buffer.Length);
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

    public ValueTask SetLengthAsync(long length)
    {
        try
        {
            RandomAccess.SetLength(_handle, length);
            _logicalLength = length;
            _padded = false;
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
    }

    public ValueTask FlushAsync()
    {
        try
        {
            TrimPadding();
            RandomAccess.FlushToDisk(_handle);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_handle.IsClosed)
            {
                TrimPadding();
            }
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
        finally
        {
            _handle.Dispose();
            ((IDisposable?)_aligned)?.Dispose();
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the zero padding written to satisfy sector alignment so the file ends exactly where the data ends.</summary>
    private void TrimPadding()
    {
        if (_padded)
        {
            RandomAccess.SetLength(_handle, _logicalLength);
            _padded = false;
        }
    }
}
