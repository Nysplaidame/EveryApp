using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace EveryApp.Core.FileSystem;

internal enum GateDisposeResult
{
    AlreadyDisposed,
    Drained,
    TimedOut,
}

/// <summary>
/// Orders operations on a handle: many shared operations (reads/writes) may run together, exclusive ones (set length, flush, dispose)
/// wait for them to finish. After dispose starts, new operations throw <see cref="ObjectDisposedException"/>.
/// The semaphores are deliberately never disposed: they hold no unmanaged resources, and disposing them could strand waiters.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim without AvailableWaitHandle holds nothing to release; disposing would strand waiters.")]
internal sealed class IoGate(string name)
{
    private const int MaxShared = 64;
    private readonly SemaphoreSlim _shared = new(MaxShared, MaxShared);
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private int _disposing;

    public bool IsDisposing => Volatile.Read(ref _disposing) != 0;

    public async ValueTask EnterSharedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposing, name);
        await _shared.WaitAsync(ct).ConfigureAwait(false);
        if (IsDisposing)
        {
            _shared.Release();
            throw new ObjectDisposedException(name);
        }
    }

    public void ExitShared() => _shared.Release();

    /// <summary>Waits until no shared operation is running and blocks new ones until <see cref="ExitExclusive"/>.</summary>
    public async ValueTask EnterExclusiveAsync()
    {
        await _exclusive.WaitAsync().ConfigureAwait(false);
        for (var i = 0; i < MaxShared; i++)
        {
            await _shared.WaitAsync().ConfigureAwait(false);
        }

        if (IsDisposing)
        {
            ExitExclusive();
            throw new ObjectDisposedException(name);
        }
    }

    public void ExitExclusive()
    {
        _shared.Release(MaxShared);
        _exclusive.Release();
    }

    /// <summary>
    /// Marks the handle disposed, asks <paramref name="cancelPendingIo"/> to abort in-flight I/O, then waits (bounded by
    /// <paramref name="timeout"/>) for it to finish. On <see cref="GateDisposeResult.Drained"/> the caller owns the gate and must call
    /// <see cref="EndDispose"/>. On <see cref="GateDisposeResult.TimedOut"/> I/O may still be using the buffers: the caller must not free them.
    /// </summary>
    public async ValueTask<GateDisposeResult> BeginDisposeAsync(Action cancelPendingIo, TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return GateDisposeResult.AlreadyDisposed;
        }

        cancelPendingIo();
        var clock = Stopwatch.StartNew();
        if (!await _exclusive.WaitAsync(timeout).ConfigureAwait(false))
        {
            return GateDisposeResult.TimedOut;
        }

        var acquired = 0;
        while (acquired < MaxShared)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !await _shared.WaitAsync(remaining).ConfigureAwait(false))
            {
                _shared.Release(acquired);
                _exclusive.Release();
                return GateDisposeResult.TimedOut;
            }

            acquired++;
        }

        return GateDisposeResult.Drained;
    }

    /// <summary>Releases the gate so callers that were waiting wake up, see IsDisposing and throw instead of hanging.</summary>
    public void EndDispose() => ExitExclusive();
}
