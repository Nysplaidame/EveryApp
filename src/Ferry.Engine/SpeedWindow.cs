namespace Ferry.Engine;

/// <summary>Moving average of transfer speed over the last 10 seconds (samples are timestamp and total bytes so far). Not thread safe.</summary>
internal sealed class SpeedWindow(long ticksPerSecond)
{
    private const int WindowSeconds = 10;
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private (long Timestamp, long Bytes) _last;

    public double BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0;
            }

            var first = _samples.Peek();
            var elapsed = _last.Timestamp - first.Timestamp;
            return elapsed <= 0 ? 0 : (_last.Bytes - first.Bytes) * (double)ticksPerSecond / elapsed;
        }
    }

    public void Add(long timestamp, long bytes)
    {
        _last = (timestamp, bytes);
        _samples.Enqueue(_last);
        var window = WindowSeconds * ticksPerSecond;
        while (_samples.Count > 2 && timestamp - _samples.Peek().Timestamp > window)
        {
            _samples.Dequeue();
        }
    }
}
