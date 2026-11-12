namespace Ferry.Engine;

/// <summary>Job-wide progress: totals, the current file, the phase and a 10-second speed average. Thread safe; never lets the caller's callback break the copy.</summary>
internal sealed class ProgressTracker(IProgress<JobProgress> sink, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly SpeedWindow _speed = new(time.TimestampFrequency);
    private long _bytesTotal;
    private long _bytesDone;
    private long _currentFileBytes;
    private int _filesTotal;
    private int _filesDone;
    private string _phase = "Checking";
    private string? _currentFile;

    public void AddToTotal(int files, long bytes)
    {
        lock (_lock)
        {
            _filesTotal += files;
            _bytesTotal += bytes;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_lock)
        {
            _phase = phase;
        }

        Report();
    }

    public void StartFile(string key)
    {
        lock (_lock)
        {
            _currentFile = key;
            _currentFileBytes = 0;
        }

        Report();
    }

    public void ResetCurrentFile()
    {
        lock (_lock)
        {
            _currentFileBytes = 0;
        }
    }

    public void EndFile(long length) => CompleteFiles(1, length);

    public void CompleteFiles(int files, long bytes)
    {
        lock (_lock)
        {
            _currentFileBytes = 0;
            _bytesDone += bytes;
            _filesDone += files;
        }

        Report();
    }

    /// <summary>Adapter for <see cref="FileCopier"/>'s per-file progress.</summary>
    public IProgress<FileProgress> FileSink() => new FileProgressSink(this);

    public void Report()
    {
        JobProgress snapshot;
        lock (_lock)
        {
            var done = _bytesDone + _currentFileBytes;
            _speed.Add(time.GetTimestamp(), done);
            var speed = _speed.BytesPerSecond;
            TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _bytesTotal - done) / speed) : null;
            snapshot = new JobProgress(done, _bytesTotal, _filesDone, _filesTotal, speed, remaining, _currentFile, _phase);
        }

#pragma warning disable CA1031 // The progress callback belongs to the caller; it must never break the copy.
        try
        {
            sink.Report(snapshot);
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void FileBytes(long done)
    {
        lock (_lock)
        {
            _currentFileBytes = done;
        }

        Report();
    }

    private sealed class FileProgressSink(ProgressTracker tracker) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => tracker.FileBytes(value.Done);
    }
}
