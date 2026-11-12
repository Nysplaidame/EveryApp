using System.Globalization;
using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>
/// Waits out brief failures (a network drive dropping for a moment, another program holding a file) and tries the same operation again.
/// Waits grow 1 s, 2 s, 4 s … up to 30 s each; it gives up after <c>maxAttempts</c> tries or once the waits add up to <c>maxWait</c>
/// (5 minutes by default), whichever comes first. Each <see cref="RunAsync{T}"/> call has its own budget. Thread safe (holds no state).
/// </summary>
public sealed class RetryPolicy
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromMinutes(5);

    private readonly int _maxAttempts;
    private readonly TimeSpan _maxWait;
    private readonly TimeProvider _time;

    /// <param name="maxAttempts">Most tries of the operation, the first included.</param>
    /// <param name="maxWait">Most total time spent waiting between tries (default 5 minutes).</param>
    /// <param name="time">Clock for the waits (tests pass a fake one; default: the system clock).</param>
    public RetryPolicy(int maxAttempts = 30, TimeSpan? maxWait = null, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        var wait = maxWait ?? DefaultMaxWait;
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero, nameof(maxWait));
        _maxAttempts = maxAttempts;
        _maxWait = wait;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The failures worth waiting out: a lost network connection and a file another program is using.</summary>
    public static bool IsTransient(IoErrorKind kind) => kind is IoErrorKind.NetworkLost or IoErrorKind.SharingViolation;

    /// <summary>What to show while waiting after a failure of this kind.</summary>
    public static string WaitingMessage(IoErrorKind kind) =>
        kind == IoErrorKind.SharingViolation ? CopyMessages.WaitingForFile : CopyMessages.WaitingForNetwork;

    /// <summary>
    /// Runs <paramref name="op"/>; when it throws a <see cref="FileSystemException"/> whose kind <paramref name="isTransient"/> accepts, waits
    /// and runs it again. Any other exception passes straight through. When the tries or the waiting time run out, throws
    /// <see cref="RetryGaveUpException"/> carrying the last failure and how long was spent waiting. Cancellation stops a wait at once.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> op, Func<IoErrorKind, bool> isTransient, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(isTransient);
        var waited = TimeSpan.Zero;
        var delay = FirstDelay;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await op().ConfigureAwait(false);
                if (waited > TimeSpan.Zero)
                {
                    status?.Report(CopyMessages.Copying);
                }

                return result;
            }
            catch (FileSystemException ex) when (isTransient(ex.Kind))
            {
                ct.ThrowIfCancellationRequested();
                if (attempt >= _maxAttempts || waited >= _maxWait)
                {
                    if (waited == TimeSpan.Zero)
                    {
                        throw; // never waited: the plain failure says it all
                    }

                    throw new RetryGaveUpException(ex, waited);
                }

                var wait = delay < _maxWait - waited ? delay : _maxWait - waited;
                status?.Report(WaitingMessage(ex.Kind));
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                waited += wait;
                delay = delay * 2 < LongestDelay ? delay * 2 : LongestDelay;
            }
        }
    }
}

/// <summary>A <see cref="RetryPolicy"/> waited as long as it may and the operation still failed.</summary>
public sealed class RetryGaveUpException : Exception
{
    public RetryGaveUpException(FileSystemException last, TimeSpan waited)
        : base(string.Create(CultureInfo.InvariantCulture, $"Gave up after waiting {waited}."), last)
    {
        Last = last;
        Waited = waited;
    }

    /// <summary>The failure the last try ended with.</summary>
    public FileSystemException Last { get; }

    /// <summary>Total time spent waiting between tries.</summary>
    public TimeSpan Waited { get; }
}
