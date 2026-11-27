namespace Ferry.Engine;

/// <summary>Job-wide progress: totals, the current file, the phase and a 10-second speed average. Thread safe; never lets the caller's callback break the copy.</summary>
internal sealed class ProgressTracker(IProgress<JobProgress> sink, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly SpeedWindow _speed = new(time.TimestampFrequency);
    private long _bytesTotal;
    private long _bytesDone;
    private int _filesTotal;
    private int _filesDone;
    private string _phase = "Checking";
    private readonly List<FileSlot> _slots = []; // in start order; keyed by the slot object, never by file key
    private JobProgress? _pending; // the newest snapshot nobody has delivered yet (guarded by _lock)
    private bool _delivering; // true while one thread is handing snapshots to the sink (guarded by _lock)

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

    /// <summary>Starts tracking one file; the returned slot reports that file's bytes and phase until <see cref="FileSlot.End"/>.</summary>
    public FileSlot StartFile(string key)
    {
        var slot = new FileSlot(this, key);
        lock (_lock)
        {
            _slots.Add(slot);
        }

        Report();
        return slot;
    }

    public void CompleteFiles(int files, long bytes)
    {
        lock (_lock)
        {
            _bytesDone += bytes;
            _filesDone += files;
        }

        Report();
    }

    public IProgress<string> StatusSink() => new StatusProgressSink(this);

    private sealed class StatusProgressSink(ProgressTracker tracker) : IProgress<string>
    {
        public void Report(string value) => tracker.SetPhase(value);
    }

    /// <summary>
    /// Builds a snapshot and hands it to the sink. Snapshots reach the sink in the order they were built, one at a time, so the totals never
    /// step backwards. One thread at a time delivers; any other thread just leaves its newer snapshot for it and returns at once (an older
    /// one nobody delivered yet is replaced), so a slow or re-entrant sink can never block or deadlock another thread.
    /// </summary>
    public void Report()
    {
        lock (_lock)
        {
            var inFlightBytes = 0L;
            var allVerifying = _slots.Count > 0;
            foreach (var slot in _slots)
            {
                inFlightBytes += slot.Done;
                allVerifying &= slot.IsVerifying;
            }

            var done = _bytesDone + inFlightBytes;
            _speed.Add(time.GetTimestamp(), done);
            var speed = _speed.BytesPerSecond;
            TimeSpan? remaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, _bytesTotal - done) / speed) : null;
            _pending = new JobProgress(done, _bytesTotal, _filesDone, _filesTotal, speed, remaining, _slots.Count > 0 ? _slots[^1].Key : null, allVerifying ? "Verifying" : _phase) { InFlight = _slots.Count };
            if (_delivering)
            {
                return; // the thread delivering now takes this snapshot next
            }

            _delivering = true;
        }

        while (true)
        {
            JobProgress snapshot;
            lock (_lock)
            {
                if (_pending is null)
                {
                    _delivering = false;
                    return;
                }

                snapshot = _pending;
                _pending = null;
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
    }

    /// <summary>One file being copied. Each slot keeps its own bytes and verifying flag; every member is safe to call from any thread.</summary>
    internal sealed class FileSlot
    {
        private readonly ProgressTracker _tracker;

        internal FileSlot(ProgressTracker tracker, string key)
        {
            _tracker = tracker;
            Key = key;
            Bytes = new BytesSink(this);
        }

        /// <summary>Receives this file's byte progress.</summary>
        public IProgress<FileProgress> Bytes { get; }

        internal string Key { get; }

        // Guarded by the tracker's lock.
        internal long Done { get; private set; }

        internal bool IsVerifying { get; private set; }

        private bool Ended { get; set; }

        /// <summary>Marks this file as being read back to check it.</summary>
        public void Verifying() => Change(() => IsVerifying = true);

        /// <summary>A retry starts over: this file's bytes go back to zero (other files are untouched).</summary>
        public void Reset() => Change(() =>
        {
            Done = 0;
            IsVerifying = false;
            _tracker._phase = CopyMessages.Copying; // a status line must not outlive the wait
        });

        /// <summary>Counts the file as done with <paramref name="length"/> bytes and frees the slot. Safe to call twice.</summary>
        public void End(long length)
        {
            lock (_tracker._lock)
            {
                if (Ended)
                {
                    return;
                }

                Ended = true;
                _tracker._slots.Remove(this);
                _tracker._bytesDone += length;
                _tracker._filesDone++;
                _tracker._phase = CopyMessages.Copying; // a status line must not outlive the wait
            }

            _tracker.Report();
        }

        /// <summary>Frees the slot without counting the file as done (it was never copied). Safe to call twice, or after <see cref="End"/>.</summary>
        public void Abandon()
        {
            lock (_tracker._lock)
            {
                if (Ended)
                {
                    return;
                }

                Ended = true;
                _tracker._slots.Remove(this);
                _tracker._phase = CopyMessages.Copying; // a status line must not outlive the wait
            }

            _tracker.Report();
        }

        private void Change(Action change)
        {
            lock (_tracker._lock)
            {
                if (Ended)
                {
                    return;
                }

                change();
            }

            _tracker.Report();
        }

        private sealed class BytesSink(FileSlot slot) : IProgress<FileProgress>
        {
            public void Report(FileProgress value) => slot.Change(() => slot.Done = value.Done);
        }
    }
}
