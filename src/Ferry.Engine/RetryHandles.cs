using System.Runtime.CompilerServices;
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

internal sealed class RetryReadHandle(IReadHandle initial, Func<IReadHandle> reopen, RetryPolicy policy, IProgress<string>? status) : IReadHandle
{
    private IReadHandle? _handle = initial;

    public long Length { get; } = initial.Length;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
    {
        try
        {
            return await _handle!.ReadAsync(buffer, offset, ct).ConfigureAwait(false);
        }
        catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind))
        {
            return await RetryAsync(ex, buffer, offset, ct).ConfigureAwait(false);
        }
    }

    private Task<int> RetryAsync(FileSystemException failure, Memory<byte> buffer, long offset, CancellationToken ct) =>
        RetryHandles.AfterFailureAsync(policy, failure, async () =>
        {
            var old = _handle;
            _handle = null;
            await RetryHandles.CloseFailedAsync(old).ConfigureAwait(false);
            _handle = reopen();
            if (_handle.Length != Length)
            {
                throw new SourceChangedException();
            }

            return await _handle.ReadAsync(buffer, offset, ct).ConfigureAwait(false);
        }, status, ct);

    public ValueTask DisposeAsync() => _handle?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed class RetryWriteHandle(IWriteHandle initial, Func<ValueTask<IWriteHandle>> reopen, RetryPolicy policy, IProgress<string>? status, CancellationToken cancellation) : IWriteHandle
{
    private IWriteHandle? _handle = initial;

    public bool Reopened { get; private set; }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        try
        {
            await _handle!.WriteAsync(buffer, offset, ct).ConfigureAwait(false);
        }
        catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind))
        {
            await RetryWriteAsync(ex, buffer, offset, ct).ConfigureAwait(false);
        }
    }

    private Task<bool> RetryWriteAsync(FileSystemException failure, ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct) =>
        RetryAsync(failure, h => h.WriteAsync(buffer, offset, ct), ct);

    public async ValueTask FlushAsync()
    {
        try
        {
            await _handle!.FlushAsync().ConfigureAwait(false);
        }
        catch (FileSystemException ex) when (RetryPolicy.IsTransient(ex.Kind))
        {
            await RetryAsync(ex, h => h.FlushAsync(), cancellation).ConfigureAwait(false);
        }
    }

    private Task<bool> RetryAsync(FileSystemException failure, Func<IWriteHandle, ValueTask> operation, CancellationToken ct) =>
        RetryHandles.AfterFailureAsync(policy, failure, async () =>
        {
            var old = _handle;
            _handle = null;
            await RetryHandles.CloseFailedAsync(old).ConfigureAwait(false);
            _handle = await reopen().ConfigureAwait(false);
            Reopened = true;
            await operation(_handle).ConfigureAwait(false);
            return true;
        }, status, ct);

    /// <summary>Not retried: after a reconnect the copier verifies by reading the file again from the start.</summary>
    public ValueTask<int> ReadBackAsync(Memory<byte> buffer, long offset, CancellationToken ct) => _handle!.ReadBackAsync(buffer, offset, ct);

    public ValueTask SetLengthAsync(long length) => _handle!.SetLengthAsync(length);

    public string GetFinalPath() => _handle!.GetFinalPath();

    public void DeleteOnClose() => _handle!.DeleteOnClose();

    public ValueTask DisposeAsync() => _handle?.DisposeAsync() ?? ValueTask.CompletedTask;
}
