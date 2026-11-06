namespace EveryApp.Core.Scheduling;

/// <summary>
/// Runs jobs that touch the same physical drive (or NAS) one after another, and jobs on independent devices in parallel.
/// <para>
/// Fairness rule (strict FIFO per device): a job may start only when it overlaps neither any running job nor any EARLIER job
/// that is still waiting. So a later job on a free drive starts straight away, but a later job can never overtake an earlier
/// waiter it shares a device with, and a stream of overlapping later jobs cannot starve that waiter.
/// </para>
/// <para>
/// Devices are resolved synchronously inside <see cref="AcquireAsync"/> so queue order is exactly call order. The resolver
/// caches per volume, so only the first call for a volume can take a moment; call from a background thread.
/// </para>
/// </summary>
public sealed class DriveAwareScheduler
{
    internal const string WaitingReason = "Waiting: uses the same drive as another copy";

    private readonly IDeviceResolver _resolver;
    private readonly Lock _gate = new();
    private readonly List<Entry> _queue = []; // arrival order: running jobs and waiting jobs

    public DriveAwareScheduler(IDeviceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <summary>Raised once when a job has to wait. Arguments: a short job label and a plain-language reason.</summary>
    public event Action<string, string>? Waiting;

    /// <summary>
    /// Completes when the job may start. Dispose the returned handle when the job ends (disposing twice is harmless).
    /// Cancelling while waiting (or in a race with admission) removes the job and throws <see cref="OperationCanceledException"/>; a cancelled caller never receives a handle.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(string source, string destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ct.ThrowIfCancellationRequested();

        // Resolved before taking the lock: resolution never runs under it.
        var devices = _resolver.Resolve(source).Union(_resolver.Resolve(destination));
        var entry = new Entry(devices);
        lock (_gate)
        {
            _queue.Add(entry);
            Pump();
        }

        if (!entry.Started.Task.IsCompleted)
        {
            RaiseWaiting($"{source} to {destination}");
            if (ct.CanBeCanceled)
            {
                // Registered after queueing; a token cancelled in between fires the callback immediately.
                entry.CancelRegistration = ct.Register(() => Cancel(entry, ct));
            }
        }

        var lease = await entry.Started.Task.ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            // Admitted in a race with cancellation: never hand a lease to a cancelled caller.
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }

        return lease;
    }

    private void RaiseWaiting(string label)
    {
        try
        {
            Waiting?.Invoke(label, WaitingReason);
        }
        catch (Exception)
        {
            // A faulty listener must not break scheduling.
        }
    }

    private void Cancel(Entry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            if (entry.Running)
            {
                return; // already started: the caller receives the handle and owns its release
            }

            _queue.Remove(entry);
            Pump(); // jobs that were blocked only by this one can start now
        }

        entry.Started.TrySetCanceled(ct);
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (!_queue.Remove(entry))
            {
                return;
            }

            Pump();
        }
    }

    /// <summary>Starts every waiting job that overlaps nothing ahead of it. Caller holds the lock.</summary>
    private void Pump()
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            var candidate = _queue[i];
            if (candidate.Running)
            {
                continue;
            }

            var blocked = false;
            for (var j = 0; j < i; j++)
            {
                if (_queue[j].Devices.Overlaps(candidate.Devices))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                candidate.Running = true;
                // The lease is built here but handed out through the task; continuations run asynchronously so they never run under the lock.
                candidate.Started.TrySetResult(new Lease(this, candidate));
            }
        }
    }

    private sealed class Entry(DeviceSet devices)
    {
        public DeviceSet Devices { get; } = devices;

        public bool Running { get; set; }

        public TaskCompletionSource<IAsyncDisposable> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CancelRegistration { get; set; }
    }

    private sealed class Lease(DriveAwareScheduler owner, Entry entry) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                entry.CancelRegistration.Dispose();
                owner.Release(entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
