using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;

namespace Ferry.Engine.Tests;

/// <summary>Large files keep several chunks in flight on both sides (task 8). Every hold is a <see cref="TaskCompletionSource"/> gate.</summary>
public sealed class FileCopierPipelineTests
{
    private const int MiB = 1024 * 1024;
    private const int Chunk = FileCopier.DefaultBufferSizeBytes;
    private const int Depth = FileCopier.PipelineDepth;

    /// <summary>Waits for a gate; the limit only turns a broken build's hang into a failure, it never decides a passing run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transfer_KeepsSeveralReadsInFlight()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        var counter = new Concurrency();
        var reached = NewSignal();
        var release = NewSignal();
        rig.B.Hooks.BeforeRead = async (path, _, ct) =>
        {
            if (path != rig.Src)
            {
                return;
            }

            if (counter.Enter() >= Depth)
            {
                reached.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
                await Task.Yield();
            }
            finally
            {
                counter.Exit();
            }
        };

        var copy = rig.Copy(new CopyOptions(), Ct);
        await reached.Task.WaitAsync(Patience, Ct);
        Assert.Equal(Depth, counter.Started); // the first read is still held, yet all of them were issued
        release.SetResult();
        var outcome = await copy;

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(Depth, counter.Max);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_KeepsSeveralWritesInFlight()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        var counter = new Concurrency();
        var reached = NewSignal();
        var release = NewSignal();
        rig.B.Hooks.BeforeWrite = async (path, _, ct) =>
        {
            if (!FileCopierTests.IsPart(path))
            {
                return;
            }

            if (counter.Enter() >= Depth)
            {
                reached.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
                await Task.Yield();
            }
            finally
            {
                counter.Exit();
            }
        };

        var copy = rig.Copy(new CopyOptions(), Ct);
        await reached.Task.WaitAsync(Patience, Ct);
        Assert.Equal(Depth, counter.Started);
        release.SetResult();
        var outcome = await copy;

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(Depth, counter.Max);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_HashesInFileOrder_WhenReadsCompleteOutOfOrder()
    {
        using var rig = new Rig((4 * Chunk) + 513);
        var laterDone = 0;
        var othersDone = NewSignal();
        rig.B.Hooks.AfterRead = (path, offset) =>
        {
            if (path == rig.Src && offset is > 0 and < Depth * Chunk && Interlocked.Increment(ref laterDone) == Depth - 1)
            {
                othersDone.TrySetResult();
            }
        };
        rig.B.Hooks.BeforeRead = async (path, offset, ct) =>
        {
            if (path == rig.Src && offset == 0)
            {
                await othersDone.Task.WaitAsync(ct); // the first chunk arrives last
            }
        };

        var outcome = await rig.Copy(new CopyOptions { ExportChecksums = true }, Ct).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        using var hasher = new StreamingHasher(HashAlgorithmKind.XxHash3);
        hasher.Append(rig.Data);
        Assert.Equal(hasher.Finish(), outcome.SourceHash);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_TailWriteWaitsForEarlierWrites()
    {
        using var rig = new Rig((3 * Chunk) + 513); // unbuffered: the last write is short and padded by the handle
        const long TailOffset = 3L * Chunk;
        var firstHeld = 1;
        var tailWhileHeld = false;
        var tailRead = NewSignal();
        rig.B.Hooks.AfterRead = (path, offset) =>
        {
            if (path == rig.Src && offset == TailOffset)
            {
                tailRead.TrySetResult();
            }
        };
        rig.B.Hooks.BeforeWrite = async (path, offset, ct) =>
        {
            if (!FileCopierTests.IsPart(path))
            {
                return;
            }

            if (offset == TailOffset)
            {
                tailWhileHeld = Volatile.Read(ref firstHeld) == 1;
            }
            else if (offset == 0)
            {
                await tailRead.Task.WaitAsync(ct);
                for (var i = 0; i < 50; i++)
                {
                    await Task.Yield(); // room for a wrong build to issue the tail write now
                }

                Volatile.Write(ref firstHeld, 0);
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.False(tailWhileHeld, "The short tail write started while an earlier write was still running.");
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_FailureMidway_ReadFails_ReturnsEveryBuffer_AndNoIoStillRunning()
    {
        using var rig = new Rig((8 * Chunk) + 513);
        rig.B.Hooks.AfterWrite = (_, _) => { };
        rig.B.Hooks.BeforeRead = async (path, offset, ct) =>
        {
            if (path != rig.Src)
            {
                return;
            }

            if (offset == 2L * Chunk)
            {
                throw new FileSystemException(path, IoErrorKind.BadBlock, null);
            }

            if (offset == 3L * Chunk)
            {
                await Task.Delay(Timeout.Infinite, ct); // still running when the other read fails
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct).WaitAsync(Patience, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.BadBlock, outcome.Problem?.Kind);
        rig.AssertNothingLeft();
    }

    [Fact]
    public async Task Transfer_FailureMidway_WriteFails_ReturnsEveryBuffer_AndNoIoStillRunning()
    {
        using var rig = new Rig((8 * Chunk) + 513);
        rig.B.Hooks.AfterRead = (_, _) => { };
        rig.B.Hooks.BeforeWrite = async (path, offset, ct) =>
        {
            if (!FileCopierTests.IsPart(path))
            {
                return;
            }

            if (offset == Chunk)
            {
                throw new FileSystemException(path, IoErrorKind.DiskFull, null);
            }

            if (offset == 0)
            {
                await Task.Delay(Timeout.Infinite, ct); // an earlier write still running when a later one fails
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct).WaitAsync(Patience, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.DiskFull, outcome.Problem?.Kind);
        rig.AssertNothingLeft();
    }

    [Fact]
    public async Task Transfer_Cancelled_ReturnsEveryBuffer_AndNoIoStillRunning()
    {
        using var rig = new Rig((8 * Chunk) + 513);
        var reached = NewSignal();
        rig.B.Hooks.AfterRead = (_, _) => { };
        rig.B.Hooks.BeforeWrite = async (path, offset, ct) =>
        {
            if (FileCopierTests.IsPart(path) && offset == Chunk)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var copy = rig.Copy(new CopyOptions(), cts.Token);
        await reached.Task.WaitAsync(Patience, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(Patience, Ct));
        rig.AssertNothingLeft();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transfer_ConnectionDropsWithSeveralInFlight_ReopensOnceAndOutputIsIdentical(bool onWrite)
    {
        using var rig = new Rig((6 * Chunk) + 513);
        if (onWrite)
        {
            rig.B.Faults.FailWriteWhere(FileCopierTests.IsPart, 2L * Chunk, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        }
        else
        {
            rig.B.Faults.FailReadRange(rig.Src, 2L * Chunk, 1, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        }

        var entered = 0;
        var allIn = NewSignal();
        Func<string, long, CancellationToken, ValueTask> hold = async (path, offset, ct) =>
        {
            if ((onWrite ? FileCopierTests.IsPart(path) : path == rig.Src) && offset < Depth * Chunk)
            {
                if (Interlocked.Increment(ref entered) == Depth)
                {
                    allIn.TrySetResult();
                }

                await allIn.Task.WaitAsync(ct); // all of the first chunks are in flight when the handle dies
            }
        };
        if (onWrite)
        {
            rig.B.Hooks.BeforeWrite = hold;
        }
        else
        {
            rig.B.Hooks.BeforeRead = hold;
        }

        var sourceOpens = 0;
        rig.B.Hooks.Opened = path =>
        {
            if (path == rig.Src)
            {
                Interlocked.Increment(ref sourceOpens);
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct, time: new AutoAdvancingTime()).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        if (onWrite)
        {
            Assert.Equal(1, rig.B.Faults.ReopenedWrites); // one reopen, however many writes were on the dead handle
        }
        else
        {
            Assert.Equal(2, Volatile.Read(ref sourceOpens)); // the first open and one reopen, however many reads were on the dead handle
        }

        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_WriteThatWaitedOnAnothersReopen_StillGetsRetries()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        rig.B.Faults.FailWriteWhere(FileCopierTests.IsPart, 2L * Chunk, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        const long Waiter = 3L * Chunk;
        var entered = 0;
        var waiterCalls = 0;
        var allIn = NewSignal();
        var reopened = NewSignal();
        rig.B.Hooks.Opened = path =>
        {
            if (FileCopierTests.IsPart(path))
            {
                reopened.TrySetResult();
            }
        };
        rig.B.Hooks.BeforeWrite = async (path, offset, ct) =>
        {
            if (!FileCopierTests.IsPart(path) || offset >= Depth * Chunk)
            {
                return;
            }

            var call = offset == Waiter ? Interlocked.Increment(ref waiterCalls) : 0;
            var n = call <= 1 ? Interlocked.Increment(ref entered) : int.MaxValue;
            if (n <= Depth)
            {
                if (n == Depth)
                {
                    allIn.TrySetResult();
                }

                await allIn.Task.WaitAsync(ct); // the first chunks are all in flight when the part's handle dies
            }

            if (call == 1)
            {
                await reopened.Task.WaitAsync(ct); // fails on the old handle only after another write has reopened the part
                throw new FileSystemException(path, IoErrorKind.NetworkLost, null);
            }

            if (call == 2)
            {
                throw new FileSystemException(path, IoErrorKind.NetworkLost, null); // and the connection drops again on the new handle
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct, time: new AutoAdvancingTime()).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(3, Volatile.Read(ref waiterCalls));
        Assert.Equal(2, rig.B.Faults.ReopenedWrites);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_PartReplacedWhileDisconnected_IsNeitherWrittenNorDeleted()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        rig.B.Faults.FailWriteWhere(FileCopierTests.IsPart, 2L * Chunk, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        var decoy = FileCopierTests.Bytes(3 * Chunk, seed: 41);
        string? part = null;
        rig.B.Hooks.Opened = path =>
        {
            if (FileCopierTests.IsPart(path))
            {
                // Another file now answers to the part's name, at the same final path (like a hard link put in its place).
                part = path;
                rig.B.Memory!.AddFile(path, decoy);
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct, time: new AutoAdvancingTime()).WaitAsync(Patience, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(IoErrorKind.InvalidName, outcome.Problem?.Kind);
        Assert.NotNull(part);
        Assert.True(rig.B.Exists(part), "The file put in the part's place was deleted.");
        Assert.True(decoy.AsSpan().SequenceEqual(rig.B.ReadAll(part)), "The file put in the part's place was written to.");
        Assert.False(rig.B.Exists(rig.Dst));
        Assert.Equal(0, rig.Pool.Outstanding);
    }

    [Fact]
    public async Task Transfer_SourceSwappedJustBeforeReopen_IsRefused()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        var stamp = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        rig.B.Memory!.Clock = () => stamp;
        rig.B.AddFile(rig.Src, rig.Data); // same content again, with a time the swapped-in file will share
        rig.B.Faults.FailReadRange(rig.Src, 2L * Chunk, 1, IoErrorKind.NetworkLost, times: 1, handleDies: true);
        var opens = 0;
        rig.B.Hooks.Opened = path =>
        {
            if (path == rig.Src && Interlocked.Increment(ref opens) == 2)
            {
                // After any look-up by name and just before the reopen, a different file of the same size and time takes the name.
                rig.B.Memory.AddFile(rig.Src, FileCopierTests.Bytes(rig.Data.Length, seed: 77));
            }
        };

        var outcome = await rig.Copy(new CopyOptions(), Ct, time: new AutoAdvancingTime()).WaitAsync(Patience, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(CopyMessages.SourceChanged, outcome.Problem?.Message);
        rig.AssertNothingLeft();
    }

    [Fact]
    public async Task Transfer_RescueMode_OneReadAtATime()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        var counter = new Concurrency();
        rig.B.Hooks.BeforeRead = async (path, _, _) =>
        {
            if (path != rig.Src)
            {
                return;
            }

            counter.Enter();
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    await Task.Yield(); // room for a wrong build to start another read
                }
            }
            finally
            {
                counter.Exit();
            }
        };

        var outcome = await rig.Copy(new CopyOptions { RescueMode = true }, Ct).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        Assert.Equal(1, counter.Max);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Transfer_ProgressNeverGoesDown_AndEndsAtTheLength()
    {
        using var rig = new Rig((6 * Chunk) + 513);
        var laterDone = 0;
        var othersDone = NewSignal();
        rig.B.Hooks.AfterWrite = (path, offset) =>
        {
            if (FileCopierTests.IsPart(path) && offset is > 0 and < Depth * Chunk && Interlocked.Increment(ref laterDone) == Depth - 1)
            {
                othersDone.TrySetResult();
            }
        };
        rig.B.Hooks.BeforeWrite = async (path, offset, ct) =>
        {
            if (FileCopierTests.IsPart(path) && offset == 0)
            {
                await othersDone.Task.WaitAsync(ct); // the first write finishes last
            }
        };
        var progress = new AllProgress();

        var outcome = await rig.Copy(new CopyOptions(), Ct, progress).WaitAsync(Patience, Ct);

        Assert.True(outcome.Success, outcome.Problem?.Message);
        var seen = progress.Done;
        Assert.NotEmpty(seen);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i] >= seen[i - 1], $"Progress went from {seen[i - 1]} down to {seen[i]}.");
        }

        Assert.Equal(rig.Data.Length, seen[^1]);
        rig.AssertCopied();
    }

    [Fact]
    public async Task Verifier_HashAsync_KeepsSeveralReadsInFlight_AndHashesInOrder()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes((5 * Chunk) + 513, seed: 81);
        var path = b.PathOf("check.bin");
        b.AddFile(path, data);
        var counter = new Concurrency();
        var laterDone = 0;
        var startedWhileFirstHeld = 0;
        var othersDone = NewSignal();
        b.Hooks.AfterRead = (_, offset) =>
        {
            if (offset is > 0 and < Depth * Chunk && Interlocked.Increment(ref laterDone) == Depth - 1)
            {
                othersDone.TrySetResult();
            }
        };
        b.Hooks.BeforeRead = async (_, offset, ct) =>
        {
            counter.Enter();
            try
            {
                if (offset == 0)
                {
                    await othersDone.Task.WaitAsync(ct); // the first chunk arrives last
                    for (var i = 0; i < 20; i++)
                    {
                        await Task.Yield(); // room for a wrong build to start a fifth read now
                    }

                    Volatile.Write(ref startedWhileFirstHeld, counter.Started);
                }
            }
            finally
            {
                counter.Exit();
            }
        };

        var hash = await Verifier.HashAsync(b.Fs, path, unbuffered: true, HashAlgorithmKind.XxHash3, pool, Ct).WaitAsync(Patience, Ct);

        using var hasher = new StreamingHasher(HashAlgorithmKind.XxHash3);
        hasher.Append(data);
        Assert.Equal(hasher.Finish(), hash);
        Assert.Equal(Depth, Volatile.Read(ref startedWhileFirstHeld)); // all in flight together, and never more
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, b.Hooks.ReadsRunning);
    }

    [Fact]
    public async Task Verifier_HashAsync_SmallFile_OneReadOfItsOwnSize()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var data = FileCopierTests.Bytes(100, seed: 82);
        var path = b.PathOf("small.bin");
        b.AddFile(path, data);
        var asked = new System.Collections.Concurrent.ConcurrentQueue<int>();
        b.Hooks.ReadAsked = (_, _, size) => asked.Enqueue(size);

        var hash = await Verifier.HashAsync(b.Fs, path, unbuffered: true, HashAlgorithmKind.XxHash3, pool, Ct);

        using var hasher = new StreamingHasher(HashAlgorithmKind.XxHash3);
        hasher.Append(data);
        Assert.Equal(hasher.Finish(), hash);
        Assert.Equal(4096, Assert.Single(asked));
        Assert.Equal(1, pool.RentCount);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task Verifier_HashAsync_ReadFails_ReturnsEveryBuffer_AndNoReadStillRunning()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var path = b.PathOf("check.bin");
        b.AddFile(path, FileCopierTests.Bytes((5 * Chunk) + 513, seed: 83));
        b.Hooks.BeforeRead = async (p, offset, ct) =>
        {
            if (offset == 2L * Chunk)
            {
                throw new FileSystemException(p, IoErrorKind.BadBlock, null);
            }

            if (offset == 3L * Chunk)
            {
                await Task.Delay(Timeout.Infinite, ct); // still running when the other read fails
            }
        };

        var ex = await Assert.ThrowsAsync<FileSystemException>(
            () => Verifier.HashAsync(b.Fs, path, unbuffered: true, HashAlgorithmKind.XxHash3, pool, Ct).WaitAsync(Patience, Ct));

        Assert.Equal(IoErrorKind.BadBlock, ex.Kind);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, b.Hooks.ReadsRunning);
    }

    [Fact]
    public async Task Verifier_HashAsync_Cancelled_ReturnsEveryBuffer_AndNoReadStillRunning()
    {
        using var b = Backend.Create(Backend.InMemory);
        using var pool = new BufferPool();
        var path = b.PathOf("check.bin");
        b.AddFile(path, FileCopierTests.Bytes((5 * Chunk) + 513, seed: 84));
        var entered = 0;
        var allIn = NewSignal();
        b.Hooks.BeforeRead = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref entered) == Depth)
            {
                allIn.TrySetResult();
            }

            await Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var hash = Verifier.HashAsync(b.Fs, path, unbuffered: true, HashAlgorithmKind.XxHash3, pool, cts.Token);
        await allIn.Task.WaitAsync(Patience, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hash.WaitAsync(Patience, Ct));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, b.Hooks.ReadsRunning);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Counts operations running at once and the most seen together.</summary>
    private sealed class Concurrency
    {
        private int _now;
        private int _max;
        private int _started;

        public int Max => Volatile.Read(ref _max);

        public int Started => Volatile.Read(ref _started);

        public int Enter()
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _now);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            return now;
        }

        public void Exit() => Interlocked.Decrement(ref _now);
    }

    /// <summary>Every progress value in the order it was reported (thread safe).</summary>
    private sealed class AllProgress : IProgress<FileProgress>
    {
        private readonly Lock _lock = new();
        private readonly List<long> _done = [];

        public List<long> Done
        {
            get
            {
                lock (_lock)
                {
                    return [.. _done];
                }
            }
        }

        public void Report(FileProgress value)
        {
            lock (_lock)
            {
                _done.Add(value.Done);
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(int size)
        {
            Data = FileCopierTests.Bytes(size, seed: size % 1000);
            Src = B.PathOf("src.bin");
            Dst = B.PathOf("dst.bin");
            B.AddFile(Src, Data);
        }

        public Backend B { get; } = Backend.Create(Backend.InMemory);

        public BufferPool Pool { get; } = new();

        public byte[] Data { get; }

        public string Src { get; }

        public string Dst { get; }

        public Task<FileCopyOutcome> Copy(CopyOptions options, CancellationToken ct, IProgress<FileProgress>? progress = null, TimeProvider? time = null) =>
            new FileCopier(B.Fs, Pool, time ?? TimeProvider.System).CopyAsync(Src, Dst, options, new PauseGate(), progress ?? new LatestProgress(), ct);

        public void AssertCopied()
        {
            Assert.True(Data.AsSpan().SequenceEqual(B.ReadAll(Dst)), "Destination content differs from the source.");
            Assert.Empty(B.PartFiles());
            Assert.Equal(0, Pool.Outstanding);
            Assert.Equal(0, B.Hooks.ReadsRunning);
            Assert.Equal(0, B.Hooks.WritesRunning);
        }

        public void AssertNothingLeft()
        {
            Assert.Equal(0, B.Hooks.ReadsRunning);
            Assert.Equal(0, B.Hooks.WritesRunning);
            Assert.Equal(0, Pool.Outstanding);
            Assert.Empty(B.PartFiles());
            Assert.False(B.Exists(Dst));
        }

        public void Dispose()
        {
            Pool.Dispose();
            B.Dispose();
        }
    }
}
