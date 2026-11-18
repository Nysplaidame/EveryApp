using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Tests.Fakes;

/// <summary>
/// Decorator that injects failures into any <see cref="IFileSystem"/>. Self-contained (shared with other test projects by file link).
/// A rule fires when a read or write overlaps its offset (or range). A rule made with <c>handleDies</c> also kills the handle it fired on,
/// like a network drop on SMB: every later call on that handle fails with NetworkLost, so only a fresh handle can carry on (a dying write
/// first scribbles junk over the first half of its range, like a half-done network write). To add an interface member, forward it to
/// <c>inner</c> below.
/// </summary>
public sealed class FaultInjectingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly object _lock = new();
    private readonly List<Rule> _readRules = [];
    private readonly List<Rule> _writeRules = [];
    private readonly List<Mutation> _mutations = [];
    private readonly Dictionary<string, long> _freeSpace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _written = new(StringComparer.OrdinalIgnoreCase);
    private int _openedReads;
    private int _reopenedWrites;

    /// <summary>Count of <see cref="OpenRead"/> calls.</summary>
    public int OpenedReads => Volatile.Read(ref _openedReads);

    /// <summary>Count of <see cref="OpenWriteExisting"/> calls.</summary>
    public int ReopenedWrites => Volatile.Read(ref _reopenedWrites);

    public void FailRead(string path, long atOffset, IoErrorKind kind, int times = int.MaxValue) =>
        Add(_readRules, new Rule(Exact(path), atOffset, 1, kind, times, killsHandle: false));

    /// <summary>Fails any read that touches a byte in [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>).</summary>
    public void FailReadRange(string path, long start, long length, IoErrorKind kind, int times = int.MaxValue, bool handleDies = false) =>
        Add(_readRules, new Rule(Exact(path), start, length, kind, times, handleDies));

    public void FailWrite(string path, long atOffset, IoErrorKind kind, int times = int.MaxValue) =>
        Add(_writeRules, new Rule(Exact(path), atOffset, 1, kind, times, killsHandle: false));

    /// <summary>Fails writes that touch <paramref name="atOffset"/> on any file whose path matches (e.g. a part file with a random name).</summary>
    public void FailWriteWhere(Func<string, bool> pathMatches, long atOffset, IoErrorKind kind, int times = int.MaxValue, bool handleDies = false) =>
        Add(_writeRules, new Rule(pathMatches, atOffset, 1, kind, times, handleDies));

    public void SetFreeSpace(string root, long bytes)
    {
        lock (_lock)
        {
            _freeSpace[Norm(root)] = bytes;
        }
    }

    /// <summary>Runs <paramref name="mutate"/> once, just before the first read that overlaps <paramref name="atOffset"/> (to simulate the source changing mid-copy).</summary>
    public void MutateDuringRead(string path, long atOffset, Action mutate)
    {
        lock (_lock)
        {
            _mutations.Add(new Mutation(Norm(path), atOffset, mutate));
        }
    }

    public IReadHandle OpenRead(string path, bool unbuffered)
    {
        Interlocked.Increment(ref _openedReads);
        return new ReadHandle(this, path, inner.OpenRead(path, unbuffered));
    }

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered)
    {
        if (preallocate > 0 && TryFindLimit(path, out _, out _) && preallocate > GetFreeSpace(path))
        {
            throw new FileSystemException(path, IoErrorKind.DiskFull, null);
        }

        return new WriteHandle(this, path, inner.CreateWrite(path, preallocate, unbuffered));
    }

    /// <summary>Runs just before <see cref="OpenWriteExisting"/> (e.g. to lose data an earlier, dead handle had written).</summary>
    public Action<string>? BeforeOpenWriteExisting { get; set; }

    public IWriteHandle OpenWriteExisting(string path, bool unbuffered)
    {
        Interlocked.Increment(ref _reopenedWrites);
        BeforeOpenWriteExisting?.Invoke(path);
        return new WriteHandle(this, path, inner.OpenWriteExisting(path, unbuffered));
    }

    public FileEntry GetEntry(string path) => inner.GetEntry(path);

    public FileEntry GetEntryFollowingLinks(string path) => inner.GetEntryFollowingLinks(path);

    public IEnumerable<FileEntry> EnumerateChildren(string directory) => inner.EnumerateChildren(directory);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string destination, bool overwrite) => inner.Move(source, destination, overwrite);

    public void Delete(string path) => inner.Delete(path);

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc) =>
        inner.SetMetadata(path, attributes, creationUtc, lastWriteUtc);

    public long GetFreeSpace(string path)
    {
        lock (_lock)
        {
            if (TryFindLimit(path, out var root, out var limit))
            {
                return Math.Max(0, limit - _written.GetValueOrDefault(root));
            }
        }

        return inner.GetFreeSpace(path);
    }

    private bool TryFindLimit(string path, out string root, out long limit)
    {
        var n = Norm(path);
        lock (_lock)
        {
            foreach (var (r, bytes) in _freeSpace.OrderByDescending(kv => kv.Key.Length))
            {
                if (n.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                {
                    root = r;
                    limit = bytes;
                    return true;
                }
            }
        }

        root = string.Empty;
        limit = 0;
        return false;
    }

    /// <summary>Counts written bytes against the limit set by <see cref="SetFreeSpace"/>; throws DiskFull when it would be exceeded.</summary>
    private void ChargeSpace(string path, int length)
    {
        lock (_lock)
        {
            if (!TryFindLimit(path, out var root, out var limit))
            {
                return;
            }

            var used = _written.GetValueOrDefault(root);
            if (used + length > limit)
            {
                throw new FileSystemException(path, IoErrorKind.DiskFull, null);
            }

            _written[root] = used + length;
        }
    }

    public int GetSectorSize(string path) => inner.GetSectorSize(path);

    public SecurityCopyResult CopySecurity(string source, string target) => inner.CopySecurity(source, target);

    public void CopyAlternateStreams(string source, string target, bool allStreams, CancellationToken ct) =>
        inner.CopyAlternateStreams(source, target, allStreams, ct);

    public string GetFinalPath(string path) => inner.GetFinalPath(path);

    public string? GetVolumeId(string path) => inner.GetVolumeId(path);

    public bool DeleteVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, long expectedLength, DateTime expectedLastWriteUtc, string? mustBeUnder) =>
        inner.DeleteVerified(path, expectedFileId, expectedVolumeSerial, expectedLength, expectedLastWriteUtc, mustBeUnder);

    public bool DeleteFileContained(string path, string mustBeUnder) => inner.DeleteFileContained(path, mustBeUnder);

    public bool IsContained(string path, string root) => inner.IsContained(path, root);

    public bool DeleteEmptyFolderVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, string? mustBeUnder) =>
        inner.DeleteEmptyFolderVerified(path, expectedFileId, expectedVolumeSerial, mustBeUnder);

    public bool TryCloneBlocks(string source, string target) => inner.TryCloneBlocks(source, target);

    private static string Norm(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + p[8..];
        }

        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }

    private static Func<string, bool> Exact(string path)
    {
        var n = Norm(path);
        return p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase);
    }

    private void Add(List<Rule> list, Rule rule)
    {
        lock (_lock)
        {
            list.Add(rule);
        }
    }

    /// <summary>The rule that fires for this call (counted as used), or null.</summary>
    private Rule? Match(List<Rule> rules, string path, long offset, int length)
    {
        var n = Norm(path);
        lock (_lock)
        {
            foreach (var r in rules)
            {
                if (r.Remaining > 0 && r.Matches(n) && offset < r.Start + r.Length && r.Start < offset + Math.Max(length, 1))
                {
                    r.Remaining--;
                    return r;
                }
            }
        }

        return null;
    }

    private void RunMutations(string path, long offset, int length)
    {
        var n = Norm(path);
        List<Mutation> due;
        lock (_lock)
        {
            due = _mutations
                .Where(m => string.Equals(m.Path, n, StringComparison.OrdinalIgnoreCase)
                    && offset <= m.AtOffset && m.AtOffset < offset + Math.Max(length, 1))
                .ToList();
            foreach (var m in due)
            {
                _mutations.Remove(m);
            }
        }

        foreach (var m in due)
        {
            m.Action();
        }
    }

    private sealed class Rule(Func<string, bool> matches, long start, long length, IoErrorKind kind, int remaining, bool killsHandle)
    {
        public Func<string, bool> Matches { get; } = matches;

        public long Start { get; } = start;

        public long Length { get; } = length;

        public IoErrorKind Kind { get; } = kind;

        public int Remaining { get; set; } = remaining;

        public bool KillsHandle { get; } = killsHandle;
    }

    private sealed record Mutation(string Path, long AtOffset, Action Action);

    private sealed class ReadHandle(FaultInjectingFileSystem fs, string path, IReadHandle inner) : IReadHandle
    {
        private volatile bool _dead;

        public long Length => inner.Length;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
        {
            if (_dead)
            {
                throw new FileSystemException(path, IoErrorKind.NetworkLost, null);
            }

            fs.RunMutations(path, offset, buffer.Length);
            if (fs.Match(fs._readRules, path, offset, buffer.Length) is { } rule)
            {
                _dead |= rule.KillsHandle;
                throw new FileSystemException(path, rule.Kind, null);
            }

            return inner.ReadAsync(buffer, offset, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class WriteHandle(FaultInjectingFileSystem fs, string path, IWriteHandle inner) : IWriteHandle
    {
        private volatile bool _dead;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
        {
            ThrowIfDead();
            if (fs.Match(fs._writeRules, path, offset, buffer.Length) is { } rule)
            {
                if (rule.KillsHandle)
                {
                    // A half-done network write: junk lands on disk, then the connection drops for good on this handle. The junk keeps the
                    // range's alignment rules (whole sectors) so it is valid on unbuffered handles too.
                    _dead = true;
                    var junkLength = buffer.Length / 2 / 4096 * 4096;
                    if (junkLength > 0)
                    {
                        var junk = new byte[junkLength];
                        junk.AsSpan().Fill(0xEE);
                        await inner.WriteAsync(junk, offset, ct).ConfigureAwait(false);
                    }
                }

                throw new FileSystemException(path, rule.Kind, null);
            }

            fs.ChargeSpace(path, buffer.Length);
            await inner.WriteAsync(buffer, offset, ct).ConfigureAwait(false);
        }

        public ValueTask SetLengthAsync(long length)
        {
            ThrowIfDead();
            return inner.SetLengthAsync(length);
        }

        public ValueTask FlushAsync()
        {
            ThrowIfDead();
            return inner.FlushAsync();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public string GetFinalPath()
        {
            ThrowIfDead();
            return inner.GetFinalPath();
        }

        public void DeleteOnClose() => inner.DeleteOnClose();

        private void ThrowIfDead()
        {
            if (_dead)
            {
                throw new FileSystemException(path, IoErrorKind.NetworkLost, null);
            }
        }
    }
}
