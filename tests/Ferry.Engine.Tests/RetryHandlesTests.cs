using EveryApp.Core.FileSystem;

namespace Ferry.Engine.Tests;

/// <summary>Once a reopened handle fails its identity check, the retry handle stops: no call reopens it again. Gates only, no timing.</summary>
public sealed class RetryHandlesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_ReopenFailsItsCheck_WithSeveralInFlight_ReopensOnce_AndEveryCallGetsThatFailure()
    {
        var old = new GatedReadHandle(100);
        var reopens = 0;
        var handle = new RetryReadHandle(
            old,
            () =>
            {
                Interlocked.Increment(ref reopens);
                return ValueTask.FromException<IReadHandle>(new SourceChangedException()); // a different file now answers to the name
            },
            new RetryPolicy(time: new AutoAdvancingTime()),
            null);
        var buffer = new byte[10];
        var first = handle.ReadAsync(buffer, 0, Ct).AsTask();
        var second = handle.ReadAsync(buffer, 10, Ct).AsTask();

        old.Fail(0);
        var failure = await Assert.ThrowsAsync<SourceChangedException>(() => first.WaitAsync(Patience, Ct));
        old.Fail(10); // the read that was on the same dead handle fails only now, after the check has failed
        Assert.Same(failure, await Assert.ThrowsAsync<SourceChangedException>(() => second.WaitAsync(Patience, Ct)));
        Assert.Same(failure, await Assert.ThrowsAsync<SourceChangedException>(() => handle.ReadAsync(buffer, 20, Ct).AsTask().WaitAsync(Patience, Ct)));

        Assert.Equal(1, Volatile.Read(ref reopens));
    }

    [Fact]
    public async Task Write_ReopenFailsItsCheck_WithSeveralInFlight_ReopensOnce_AndEveryCallGetsThatFailure()
    {
        var old = new GatedWriteHandle();
        var reopens = 0;
        var handle = new RetryWriteHandle(
            old,
            () =>
            {
                Interlocked.Increment(ref reopens);
                return ValueTask.FromException<IWriteHandle>(new FileSystemException("part", IoErrorKind.InvalidName, null)); // name points elsewhere
            },
            new RetryPolicy(time: new AutoAdvancingTime()),
            null,
            Ct);
        var buffer = new byte[10];
        var first = handle.WriteAsync(buffer, 0, Ct).AsTask();
        var second = handle.WriteAsync(buffer, 10, Ct).AsTask();

        old.Fail(0);
        var failure = await Assert.ThrowsAsync<FileSystemException>(() => first.WaitAsync(Patience, Ct));
        Assert.Equal(IoErrorKind.InvalidName, failure.Kind);
        old.Fail(10); // the write that was on the same dead handle fails only now, after the check has failed
        Assert.Same(failure, await Assert.ThrowsAsync<FileSystemException>(() => second.WaitAsync(Patience, Ct)));
        Assert.Same(failure, await Assert.ThrowsAsync<FileSystemException>(() => handle.WriteAsync(buffer, 20, Ct).AsTask().WaitAsync(Patience, Ct)));

        Assert.Equal(1, Volatile.Read(ref reopens));
    }

    private static FileSystemException ConnectionLost() => new("file", IoErrorKind.NetworkLost, null);

    /// <summary>Every call waits on a gate for its offset until the test fails it (an offset never gated completes at once).</summary>
    private sealed class Gates
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<long, TaskCompletionSource> _gates = [];

        public Task For(long offset)
        {
            lock (_lock)
            {
                return Get(offset).Task;
            }
        }

        public void Fail(long offset)
        {
            lock (_lock)
            {
                Get(offset).SetException(ConnectionLost());
            }
        }

        private TaskCompletionSource Get(long offset)
        {
            if (!_gates.TryGetValue(offset, out var gate))
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[offset] = gate;
            }

            return gate;
        }
    }

    private sealed class GatedReadHandle(long length) : IReadHandle
    {
        private readonly Gates _gates = new();

        public long Length => length;

        public void Fail(long offset) => _gates.Fail(offset);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
        {
            await _gates.For(offset).WaitAsync(ct);
            return 0;
        }

        public FileEntry GetEntry() => new(@"\\?\C:\work\src", length, FileAttributes.Normal, DateTime.UnixEpoch, DateTime.UnixEpoch, false, 1, 1);

        public string GetFinalPath() => @"\\?\C:\work\src";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedWriteHandle : IWriteHandle
    {
        private readonly Gates _gates = new();

        public void Fail(long offset) => _gates.Fail(offset);

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct) => await _gates.For(offset).WaitAsync(ct);

        public ValueTask<int> ReadBackAsync(Memory<byte> buffer, long offset, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask SetLengthAsync(long length) => ValueTask.CompletedTask;

        public ValueTask FlushAsync() => ValueTask.CompletedTask;

        public string GetFinalPath() => @"\\?\C:\work\part";

        public FileIdentity GetIdentity() => new(1, 1);

        public void DeleteOnClose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
