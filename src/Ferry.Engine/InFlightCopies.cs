using System.Runtime.ExceptionServices;

namespace Ferry.Engine;

/// <summary>
/// The copies of one root that are still running: free places for copies, the target names they hold, and the first unexpected error. The job
/// decides every file one at a time (the dispatcher) and hands only the copy itself to this class. With one place, the copy runs right in the
/// dispatcher, exactly as a one-at-a-time job always did.
/// </summary>
internal sealed class InFlightCopies : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _free;
    private readonly Dictionary<string, Task> _targets = new(StringComparer.OrdinalIgnoreCase); // full target path to that copy's end
    private readonly List<Task> _running = [];
    private Exception? _failure;

    /// <param name="workers">How many copies may run at once (1: one at a time, in the dispatcher).</param>
    public InFlightCopies(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);
        Workers = workers;
        _free = new SemaphoreSlim(workers, workers);
    }

    /// <summary>How many copies may run at once.</summary>
    public int Workers { get; }

    /// <summary>True once a copy ended with an error nothing handled; the dispatcher then starts no new file.</summary>
    public bool Failed => Volatile.Read(ref _failure) is not null;

    /// <summary>Waits for a free place (nothing to wait for with one place). Call <see cref="Start"/> or <see cref="GiveBack"/> after.</summary>
    public Task WaitForPlaceAsync(CancellationToken ct) => Workers == 1 ? Task.CompletedTask : _free.WaitAsync(ct);

    /// <summary>Gives back a place taken by <see cref="WaitForPlaceAsync"/> when no copy was started with it.</summary>
    public void GiveBack()
    {
        if (Workers > 1)
        {
            _free.Release();
        }
    }

    /// <summary>True when <paramref name="target"/> (a full path) is the name chosen for a copy that is still running.</summary>
    public bool IsReserved(string target)
    {
        lock (_lock)
        {
            return _targets.ContainsKey(target);
        }
    }

    /// <summary>Ends when the copy that holds <paramref name="target"/> has ended (at once if none does).</summary>
    public Task EndOf(string target)
    {
        lock (_lock)
        {
            return _targets.TryGetValue(target, out var end) ? end : Task.CompletedTask;
        }
    }

    /// <summary>Ends when every copy still running into <paramref name="folder"/> (a full path) has ended.</summary>
    public Task EndOfAllIn(string folder)
    {
        lock (_lock)
        {
            return _targets.Count == 0
                ? Task.CompletedTask
                : Task.WhenAll([.. _targets.Where(t => string.Equals(Path.GetDirectoryName(t.Key), folder, StringComparison.OrdinalIgnoreCase)).Select(t => t.Value)]);
        }
    }

    /// <summary>
    /// Runs <paramref name="copy"/> in the place taken by <see cref="WaitForPlaceAsync"/>, holding <paramref name="target"/> until it ends.
    /// With one place it runs here and its errors come straight out; otherwise it runs on its own and this returns at once.
    /// <paramref name="copy"/> must end the file's progress itself, whatever happens.
    /// </summary>
    public async Task StartAsync(string target, Func<Task> copy)
    {
        if (Workers == 1)
        {
            await copy().ConfigureAwait(false);
            return;
        }

        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _targets[target] = end.Task; // before the copy starts: it may end before this method returns
        }

        var running = Task.Run(async () =>
        {
            try
            {
                await copy().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ex, null); // the first one wins; the job rethrows it once every copy has ended
            }
            finally
            {
                lock (_lock)
                {
                    if (_targets.TryGetValue(target, out var held) && held == end.Task)
                    {
                        _targets.Remove(target);
                    }
                }

                _free.Release();
                end.TrySetResult();
            }
        });

        lock (_lock)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(running);
        }
    }

    /// <summary>Waits until every copy started here has ended. Never throws: errors are kept for <see cref="ThrowIfFailed"/>.</summary>
    public async Task WaitForAllAsync()
    {
        Task[] all;
        lock (_lock)
        {
            all = [.. _running];
        }

        await Task.WhenAll(all).ConfigureAwait(false); // each one catches its own error
    }

    /// <summary>Throws the first unexpected error a copy ended with, if any.</summary>
    public void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _free.Dispose();
}
