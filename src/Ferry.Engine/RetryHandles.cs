using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Retries only after closing a failed handle. The normal I/O path does not allocate a retry task.</summary>
internal static class RetryHandles
{
    public static Task<T> AfterFailureAsync<T>(RetryPolicy policy, FileSystemException failure, Func<Task<T>> retry, IProgress<string>? status, CancellationToken ct)
    {
        var first = true;
        return policy.RunAsync(() =>
        {
            if (first)
            {
                first = false;
                throw failure; // the policy accounts for the initial failure and waits before reopening
            }

            return retry();
        }, RetryPolicy.IsTransient, status, ct);
    }

    /// <summary>Waits until no handle is being replaced.</summary>
    public static async ValueTask WaitForSwapAsync(SemaphoreSlim swap, CancellationToken ct)
    {
        await swap.WaitAsync(ct).ConfigureAwait(false);
        swap.Release();
    }

    /// <summary>The failure to start a retry from: the operation's own when it is a file-system one, else a lost connection.</summary>
    public static FileSystemException AsTransient(Exception? failure) =>
        failure is FileSystemException fs && RetryPolicy.IsTransient(fs.Kind) ? fs : new FileSystemException(string.Empty, IoErrorKind.NetworkLost, failure);

    /// <summary>True when an operation failed only because another call replaced (and so closed) the handle it was using.</summary>
    public static bool IsSwapCasualty(Exception ex, object used, object? current, CancellationToken ct) =>
        ex is FileSystemException or ObjectDisposedException or OperationCanceledException
        && !ct.IsCancellationRequested
        && !ReferenceEquals(used, current);

    /// <summary>
    /// True when a failed reopen must stop the handle for good: the file failed its identity check or another error the policy will not
    /// retry. A lost connection or cancellation is not one.
    /// </summary>
    public static bool StopsHandle(Exception ex) =>
        ex is not OperationCanceledException && !(ex is FileSystemException fs && RetryPolicy.IsTransient(fs.Kind));

    public static async ValueTask CloseFailedAsync(IAsyncDisposable? handle)
    {
        if (handle is null)
        {
            return;
        }

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind))
        {
            // The connection has gone, but disposal still releases the local handle.
        }
    }
}

/// <summary>
/// Reopens the source after a transient failure. Several reads may run at once: the first to fail on a handle replaces it (closing the
/// old one aborts the others), and every other read that was on the old handle simply runs again on the new one. <paramref name="reopen"/>
/// checks the file it opened is still the source; once a reopen fails like that, every read (queued ones too) fails the same way without
/// opening it again.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim without AvailableWaitHandle holds nothing to release; disposing would strand waiters.")]
internal sealed class RetryReadHandle(IReadHandle initial, Func<ValueTask<IReadHandle>> reopen, RetryPolicy policy, IProgress<string>? status) : IReadHandle
{
    private readonly SemaphoreSlim _swap = new(1, 1);
    private IReadHandle? _handle = initial;
    private volatile ExceptionDispatchInfo? _stopped; // set (under _swap) when a reopen failed for good

    public long Length { get; } = initial.Length;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
    {
        while (true)
        {
            Exception? casualty = null;
            var used = Volatile.Read(ref _handle);
            if (used is not null)
            {
                try
                {
                    return await used.ReadAsync(buffer, offset, ct).ConfigureAwait(false);
                }
                catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind) && ReferenceEquals(used, Volatile.Read(ref _handle)))
                {
                    return await RetryAsync(ex, used, buffer, offset, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (RetryHandles.IsSwapCasualty(ex, used, Volatile.Read(ref _handle), ct))
                {
                    // Another read is replacing this handle: run again on the new one, with the same retries as any read.
                    casualty = ex;
                }
            }

            await RetryHandles.WaitForSwapAsync(_swap, ct).ConfigureAwait(false);
            _stopped?.Throw();
            if (Volatile.Read(ref _handle) is null)
            {
                // The last reopen failed and the policy is waiting to try again: join in with this read's own retries.
                return await RetryAsync(RetryHandles.AsTransient(casualty), used, buffer, offset, ct).ConfigureAwait(false);
            }
        }
    }

    private Task<int> RetryAsync(FileSystemException failure, IReadHandle? failed, Memory<byte> buffer, long offset, CancellationToken ct) =>
        RetryHandles.AfterFailureAsync(policy, failure, async () =>
        {
            await _swap.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _stopped?.Throw();
                var current = Volatile.Read(ref _handle);
                if (current is null || ReferenceEquals(current, failed))
                {
                    Volatile.Write(ref _handle, null);
                    await RetryHandles.CloseFailedAsync(current).ConfigureAwait(false);
                    current = await ReopenAsync().ConfigureAwait(false);
                    Volatile.Write(ref _handle, current);
                }

                failed = current; // if this attempt fails too, the next one replaces this handle
                return await current.ReadAsync(buffer, offset, ct).ConfigureAwait(false);
            }
            finally
            {
                _swap.Release();
            }
        }, status, ct);

    /// <summary>Runs under <c>_swap</c>. A reopen that fails for good stops this handle, so nothing tries the name again.</summary>
    private async ValueTask<IReadHandle> ReopenAsync()
    {
        try
        {
            var reopened = await reopen().ConfigureAwait(false);
            if (reopened.Length != Length)
            {
                await RetryHandles.CloseFailedAsync(reopened).ConfigureAwait(false);
                throw new SourceChangedException();
            }

            return reopened;
        }
        catch (Exception ex) when (RetryHandles.StopsHandle(ex))
        {
            _stopped = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    public FileEntry GetEntry() => Volatile.Read(ref _handle)!.GetEntry();

    public ValueTask DisposeAsync() => Volatile.Read(ref _handle)?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Reopens the part file after a transient failure. Several writes may run at once: the first to fail on a handle replaces it (closing the
/// old one aborts the others), and every other write that was on the old handle simply runs again on the new one. <paramref name="reopen"/>
/// checks the file it opened is still our part file; once a reopen fails like that, every write (queued ones too) fails the same way
/// without opening it again.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim without AvailableWaitHandle holds nothing to release; disposing would strand waiters.")]
internal sealed class RetryWriteHandle(IWriteHandle initial, Func<ValueTask<IWriteHandle>> reopen, RetryPolicy policy, IProgress<string>? status, CancellationToken cancellation) : IWriteHandle
{
    private readonly SemaphoreSlim _swap = new(1, 1);
    private IWriteHandle? _handle = initial;
    private volatile bool _reopened;
    private volatile ExceptionDispatchInfo? _stopped; // set (under _swap) when a reopen failed for good

    public bool Reopened => _reopened;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        while (true)
        {
            Exception? casualty = null;
            var used = Volatile.Read(ref _handle);
            if (used is not null)
            {
                try
                {
                    await used.WriteAsync(buffer, offset, ct).ConfigureAwait(false);
                    return;
                }
                catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind) && ReferenceEquals(used, Volatile.Read(ref _handle)))
                {
                    await RetryWriteAsync(ex, used, buffer, offset, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (RetryHandles.IsSwapCasualty(ex, used, Volatile.Read(ref _handle), ct))
                {
                    // Another write is replacing this handle: run again on the new one, with the same retries as any write.
                    casualty = ex;
                }
            }

            await RetryHandles.WaitForSwapAsync(_swap, ct).ConfigureAwait(false);
            _stopped?.Throw();
            if (Volatile.Read(ref _handle) is null)
            {
                // The last reopen failed and the policy is waiting to try again: join in with this write's own retries.
                await RetryWriteAsync(RetryHandles.AsTransient(casualty), used, buffer, offset, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    // Kept out of WriteAsync so the lambda's captured state is only allocated when a retry is needed.
    private Task<bool> RetryWriteAsync(FileSystemException failure, IWriteHandle? failed, ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct) =>
        RetryAsync(failure, failed, h => h.WriteAsync(buffer, offset, ct), ct);

    /// <summary>Runs alone, after every write has ended.</summary>
    public async ValueTask FlushAsync()
    {
        var used = Volatile.Read(ref _handle)!;
        try
        {
            await used.FlushAsync().ConfigureAwait(false);
        }
        catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind))
        {
            await RetryAsync(ex, used, h => h.FlushAsync(), cancellation).ConfigureAwait(false);
        }
    }

    private Task<bool> RetryAsync(FileSystemException failure, IWriteHandle? failed, Func<IWriteHandle, ValueTask> operation, CancellationToken ct) =>
        RetryHandles.AfterFailureAsync(policy, failure, async () =>
        {
            await _swap.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _stopped?.Throw();
                var current = Volatile.Read(ref _handle);
                if (current is null || ReferenceEquals(current, failed))
                {
                    Volatile.Write(ref _handle, null);
                    await RetryHandles.CloseFailedAsync(current).ConfigureAwait(false);
                    try
                    {
                        current = await reopen().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (RetryHandles.StopsHandle(ex))
                    {
                        _stopped = ExceptionDispatchInfo.Capture(ex); // nothing tries the name again
                        throw;
                    }

                    Volatile.Write(ref _handle, current);
                    _reopened = true;
                }

                failed = current; // if this attempt fails too, the next one replaces this handle
                await operation(current).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _swap.Release();
            }
        }, status, ct);

    /// <summary>Not retried: after a reconnect the copier verifies by reading the file again from the start.</summary>
    public ValueTask<int> ReadBackAsync(Memory<byte> buffer, long offset, CancellationToken ct) => Volatile.Read(ref _handle)!.ReadBackAsync(buffer, offset, ct);

    public ValueTask SetLengthAsync(long length) => Volatile.Read(ref _handle)!.SetLengthAsync(length);

    public string GetFinalPath() => Volatile.Read(ref _handle)!.GetFinalPath();

    public FileIdentity GetIdentity() => Volatile.Read(ref _handle)!.GetIdentity();

    public void DeleteOnClose() => Volatile.Read(ref _handle)!.DeleteOnClose();

    public ValueTask DisposeAsync() => Volatile.Read(ref _handle)?.DisposeAsync() ?? ValueTask.CompletedTask;
}
