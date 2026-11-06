namespace EveryApp.Core.FileSystem;

/// <summary>
/// Orders operations on a handle: many shared operations (reads/writes) may run together, exclusive ones (set length, flush, dispose)
/// wait for them to finish. After dispose starts, new operations throw <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class IoGate(string name) : IDisposable
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

    /// <summary>Marks the handle disposed and takes the exclusive lock. Returns false if disposal already happened (the caller does nothing).</summary>
    public async ValueTask<bool> BeginDisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return false;
        }

        await _exclusive.WaitAsync().ConfigureAwait(false);
        for (var i = 0; i < MaxShared; i++)
        {
            await _shared.WaitAsync().ConfigureAwait(false);
        }

        return true;
    }

    public void Dispose()
    {
        _shared.Dispose();
        _exclusive.Dispose();
    }

    public void EndDispose()
    {
        // Release so waiting callers wake up, see IsDisposing and throw instead of hanging.
        ExitExclusive();
    }
}
