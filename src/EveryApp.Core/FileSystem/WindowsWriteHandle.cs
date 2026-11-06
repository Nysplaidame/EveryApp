using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EveryApp.Core.FileSystem;

internal sealed class WindowsWriteHandle : IWriteHandle
{
    private const int BounceSize = 1024 * 1024;
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly int _sector; // 0 = buffered
    private readonly AlignedBufferPool _pool;
    private readonly IoGate _gate = new(nameof(WindowsWriteHandle));
    private readonly SemaphoreSlim _bounceLock = new(1, 1);
    private readonly object _stateLock = new();
    private AlignedBuffer? _bounce;
    private long _logicalLength; // end of the data written so far; the file is trimmed to this
    private bool _needsTrim;

    public WindowsWriteHandle(SafeFileHandle handle, string path, int sectorSize, AlignedBufferPool pool, bool preallocated)
    {
        _handle = handle;
        _path = path;
        _sector = sectorSize;
        _pool = pool;
        _needsTrim = preallocated; // end of file is ahead of the data until the final trim
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return;
        }

        var isShort = false;
        if (_sector > 0)
        {
            if (offset % _sector != 0)
            {
                throw new ArgumentException("Unbuffered write offset must be sector aligned.", nameof(offset));
            }

            isShort = buffer.Length % _sector != 0;
            if (isShort)
            {
                lock (_stateLock)
                {
                    // A short write is zero-padded to a whole sector, so it may only be the last write (it must reach the current end).
                    if (offset + buffer.Length < _logicalLength)
                    {
                        throw new InvalidOperationException("A short unbuffered write must not end before data that already exists.");
                    }
                }
            }
        }

        await _gate.EnterSharedAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sector == 0 || (!isShort && IsDirect(buffer)))
            {
                await RandomAccess.WriteAsync(_handle, buffer, offset, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteBouncedAsync(buffer, offset, ct).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _logicalLength = Math.Max(_logicalLength, offset + buffer.Length);
                _needsTrim |= isShort;
            }
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

    public async ValueTask SetLengthAsync(long length)
    {
        await _gate.EnterExclusiveAsync().ConfigureAwait(false);
        try
        {
            RandomAccess.SetLength(_handle, length);
            lock (_stateLock)
            {
                _logicalLength = length;
                _needsTrim = false;
            }
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
        finally
        {
            _gate.ExitExclusive();
        }
    }

    public async ValueTask FlushAsync()
    {
        await _gate.EnterExclusiveAsync().ConfigureAwait(false);
        try
        {
            TrimPadding();
            RandomAccess.FlushToDisk(_handle);
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            throw ErrorKinds.Translate(_path, ex);
        }
        finally
        {
            _gate.ExitExclusive();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancels and waits (bounded) for in-flight writes, then trims, then frees the handle and buffer (never while an I/O is pending).
        var result = await _gate.BeginDisposeAsync(() => NativeFileApi.CancelAll(_handle), WindowsFileSystem.DisposeTimeout).ConfigureAwait(false);
        if (result == GateDisposeResult.AlreadyDisposed)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            if (result == GateDisposeResult.Drained)
            {
                TrimPadding();
            }
        }
        catch (Exception ex) when (WindowsFileSystem.IsTranslatable(ex))
        {
            failure = ErrorKinds.Translate(_path, ex);
        }
        finally
        {
            _handle.Dispose();
            if (result == GateDisposeResult.Drained)
            {
                if (_bounce is not null)
                {
                    _pool.ReturnOrFree(_bounce);
                    _bounce = null;
                }

                _gate.EndDispose();
            }

            // TimedOut: an I/O may still own the buffer, so it is leaked on purpose rather than freed under it.
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private bool IsDirect(ReadOnlyMemory<byte> buffer) =>
        MemoryMarshal.TryGetMemoryManager<byte, AlignedBuffer>(buffer, out _, out var start, out _)
        && start % _sector == 0
        && _sector <= AlignedBuffer.Alignment;

    private async ValueTask WriteBouncedAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        await _bounceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _bounce ??= _pool.Rent(BounceSize);
            var done = 0;
            while (done < buffer.Length)
            {
                var n = Math.Min(_bounce.Size, buffer.Length - done);
                var padded = (int)((((long)n + _sector) - 1) / _sector * _sector);
                var span = _bounce.Memory.Span;
                buffer.Span.Slice(done, n).CopyTo(span);
                if (padded > n)
                {
                    span[n..padded].Clear();
                }

                await RandomAccess.WriteAsync(_handle, _bounce.Memory[..padded], offset + done, ct).ConfigureAwait(false);
                done += n;
            }
        }
        finally
        {
            _bounceLock.Release();
        }
    }

    /// <summary>Removes the zero padding written to satisfy sector alignment so the file ends exactly where the data ends. Call only with exclusive access.</summary>
    private void TrimPadding()
    {
        long length;
        lock (_stateLock)
        {
            if (!_needsTrim)
            {
                return;
            }

            length = _logicalLength;
        }

        RandomAccess.SetLength(_handle, length);
        lock (_stateLock)
        {
            _needsTrim = false;
        }
    }
}
