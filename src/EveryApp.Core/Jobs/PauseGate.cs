namespace EveryApp.Core.Jobs;

/// <summary>
/// Lets workers stop at a safe point while a job is paused. Not paused: waiting is free (no allocation, completes at once).
/// Pause and Resume can be called any number of times from any thread.
/// </summary>
public sealed class PauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource? _paused; // null while running

    public bool IsPaused => Volatile.Read(ref _paused) is not null;

    public void Pause()
    {
        lock (_lock)
        {
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? toRelease;
        lock (_lock)
        {
            toRelease = _paused;
            _paused = null;
        }

        toRelease?.TrySetResult();
    }

    /// <summary>Returns at once when not paused; otherwise completes after <see cref="Resume"/>. Throws if <paramref name="ct"/> is cancelled.</summary>
    public ValueTask WaitIfPausedAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        var current = Volatile.Read(ref _paused);
        return current is null ? ValueTask.CompletedTask : new ValueTask(WaitSlowAsync(current, ct));
    }

    private async Task WaitSlowAsync(TaskCompletionSource first, CancellationToken ct)
    {
        var current = first;
        while (true)
        {
            await current.Task.WaitAsync(ct).ConfigureAwait(false);

            // Resumed. If someone paused again before we woke, wait for that too.
            var next = Volatile.Read(ref _paused);
            if (next is null)
            {
                return;
            }

            current = next;
        }
    }
}
