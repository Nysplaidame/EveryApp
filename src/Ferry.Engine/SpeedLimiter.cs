namespace Ferry.Engine;

/// <summary>
/// Token bucket on bytes, kept as a "theoretical arrival time" (the equivalent GCRA form): each chunk is due <c>bytes / rate</c> seconds after
/// the previous one. Idle or paused time never builds up credit, so there is no burst after a pause. Not thread safe (one reader loop uses it).
/// </summary>
internal sealed class SpeedLimiter
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);
    private readonly double _bytesPerSecond;
    private readonly TimeProvider _time;
    private readonly long _start;
    private TimeSpan _due; // when the bytes handed out so far may have finished, measured from _start

    public SpeedLimiter(long bytesPerSecond, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _time = time;
        _start = time.GetTimestamp();
    }

    /// <summary>Waits until <paramref name="bytes"/> more bytes may pass. Completes at once (no allocation) when under the limit.</summary>
    public ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var now = _time.GetElapsedTime(_start);
        if (_due < now)
        {
            _due = now;
        }

        _due += TimeSpan.FromSeconds(bytes / _bytesPerSecond);
        var wait = _due - now;
        return wait <= Tolerance ? ValueTask.CompletedTask : new ValueTask(Task.Delay(wait, _time, ct));
    }
}
