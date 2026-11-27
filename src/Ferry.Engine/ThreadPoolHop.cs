using System.Runtime.CompilerServices;

namespace Ferry.Engine;

/// <summary>
/// Moves the rest of an async method onto a thread-pool thread, never onto the caller's synchronization context (a UI thread, say).
/// Used before an I/O call that Windows may run inside the call itself, so several such calls can still run at once.
/// </summary>
internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    /// <summary>
    /// True when <c>await Task.Yield()</c> already goes to the thread pool (no context, default scheduler). It is then preferred:
    /// it queues the pooled async state itself, so the hop allocates nothing.
    /// </summary>
    public static bool YieldReachesPool => SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;

    public bool IsCompleted => false;

    public ThreadPoolHop GetAwaiter() => this;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(static c => c(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false);
}
