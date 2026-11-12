using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Tests.Fakes;

/// <summary>
/// Decorator that injects failures into any <see cref="IFileSystem"/>. Self-contained (shared with other test projects by file link).
/// A rule fires when a read or write overlaps its offset. To add an interface member, forward it to <c>_inner</c> below.
/// </summary>
public sealed class FaultInjectingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly object _lock = new();
    private readonly List<Rule> _readRules = [];
    private readonly List<Rule> _writeRules = [];
    private readonly List<Mutation> _mutations = [];
    private readonly Dictionary<string, long> _freeSpace = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _written = new(StringComparer.OrdinalIgnoreCase);

    public void FailRead(string path, long atOffset, IoErrorKind kind, int times = int.MaxValue) =>
        Add(_readRules, new Rule(Norm(path), atOffset, kind, times));

    public void FailWrite(string path, long atOffset, IoErrorKind kind, int times = int.MaxValue) =>
        Add(_writeRules, new Rule(Norm(path), atOffset, kind, times));

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

    public IReadHandle OpenRead(string path, bool unbuffered) => new ReadHandle(this, path, inner.OpenRead(path, unbuffered));

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered)
    {
        if (preallocate > 0 && TryFindLimit(path, out _, out _) && preallocate > GetFreeSpace(path))
        {
            throw new FileSystemException(path, IoErrorKind.DiskFull, null);
        }

        return new WriteHandle(this, path, inner.CreateWrite(path, preallocate, unbuffered));
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

    private void Add(List<Rule> list, Rule rule)
    {
        lock (_lock)
        {
            list.Add(rule);
        }
    }

    private void Check(List<Rule> rules, string path, long offset, int length)
    {
        var n = Norm(path);
        IoErrorKind? hit = null;
        lock (_lock)
        {
            foreach (var r in rules)
            {
                if (r.Remaining > 0
                    && string.Equals(r.Path, n, StringComparison.OrdinalIgnoreCase)
                    && offset <= r.AtOffset
                    && r.AtOffset < offset + Math.Max(length, 1))
                {
                    r.Remaining--;
                    hit = r.Kind;
                    break;
                }
            }
        }

        if (hit is { } kind)
        {
            throw new FileSystemException(path, kind, null);
        }
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

    private sealed class Rule(string path, long atOffset, IoErrorKind kind, int remaining)
    {
        public string Path { get; } = path;

        public long AtOffset { get; } = atOffset;

        public IoErrorKind Kind { get; } = kind;

        public int Remaining { get; set; } = remaining;
    }

    private sealed record Mutation(string Path, long AtOffset, Action Action);

    private sealed class ReadHandle(FaultInjectingFileSystem fs, string path, IReadHandle inner) : IReadHandle
    {
        public long Length => inner.Length;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
        {
            fs.RunMutations(path, offset, buffer.Length);
            fs.Check(fs._readRules, path, offset, buffer.Length);
            return inner.ReadAsync(buffer, offset, ct);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class WriteHandle(FaultInjectingFileSystem fs, string path, IWriteHandle inner) : IWriteHandle
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
        {
            fs.Check(fs._writeRules, path, offset, buffer.Length);
            fs.ChargeSpace(path, buffer.Length);
            return inner.WriteAsync(buffer, offset, ct);
        }

        public ValueTask SetLengthAsync(long length) => inner.SetLengthAsync(length);

        public ValueTask FlushAsync() => inner.FlushAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public string GetFinalPath() => inner.GetFinalPath();

        public void DeleteOnClose() => inner.DeleteOnClose();
    }
}
