using Windows.Win32;
using Windows.Win32.System.Power;

namespace EveryApp.Core.Power;

/// <summary>
/// Keeps the PC from going to sleep (the screen may still turn off) while at least one hold is active.
/// <para>
/// SetThreadExecutionState is per thread and async code hops between threads, so the request lives on one dedicated background thread
/// that sets it, waits until the last hold is released, clears it and exits. Thread safe; holds may overlap (several jobs at once).
/// </para>
/// </summary>
public static class KeepAwake
{
    private static readonly Lock Gate = new();
    private static int _holds;
    private static ManualResetEventSlim? _release;

    /// <summary>Takes a hold. Dispose it when the work ends (disposing twice is harmless).</summary>
    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (_holds++ == 0)
            {
                var release = new ManualResetEventSlim(false);
                _release = release;
                var thread = new Thread(() => HoldUntilReleased(release)) { IsBackground = true, Name = "EveryApp keep-awake" };
                thread.Start();
            }
        }

        return new Lease();
    }

    /// <summary>For tests: the number of holds currently active.</summary>
    public static int ActiveHolds
    {
        get
        {
            lock (Gate)
            {
                return _holds;
            }
        }
    }

    private static void HoldUntilReleased(ManualResetEventSlim release)
    {
        _ = PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
        try
        {
            release.Wait();
        }
        finally
        {
            _ = PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            release.Dispose();
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            if (--_holds == 0)
            {
                _release?.Set();
                _release = null;
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }
    }
}
