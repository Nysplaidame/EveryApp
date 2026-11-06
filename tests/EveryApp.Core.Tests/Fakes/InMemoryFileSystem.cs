using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Tests.Fakes;

/// <summary>
/// Dictionary-backed <see cref="IFileSystem"/> for tests. Self-contained (shared with other test projects by file link).
/// Paths are compared case-insensitively with either slash. Parent directories must exist before a file is created.
/// File ids and volume serials are synthetic but stable for the life of an entry.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextId = 1;
    private const string LongPrefix = @"\\?\";

    public InMemoryFileSystem()
    {
        Clock = () => DateTime.UtcNow;
    }

    /// <summary>Source of timestamps for new and modified entries.</summary>
    public Func<DateTime> Clock { get; set; }

    /// <summary>Value returned by <see cref="GetFreeSpace"/>.</summary>
    public long FreeSpaceBytes { get; set; } = 1L << 40;

    /// <summary>Value returned by <see cref="GetSectorSize"/>.</summary>
    public int SectorSize { get; set; } = 512;

    /// <summary>Test helper: creates parent directories and the file in one go.</summary>
    public void AddFile(string path, byte[] content, uint reparseTag = 0)
    {
        lock (_lock)
        {
            var key = Key(path);
            EnsureDirectories(ParentOf(key));
            var node = NewNode(key, isDirectory: false);
            node.Data = content.ToArray();
            node.Length = content.Length;
            node.Tag = reparseTag;
            if (reparseTag != 0)
            {
                node.Attributes |= FileAttributes.ReparsePoint;
            }

            _nodes[key] = node;
        }
    }

    /// <summary>
    /// Test helper: adds a junction-like directory at <paramref name="path"/> whose children are those of <paramref name="targetDirectory"/>
    /// (listed under the junction's own path). It has its own file id and the reparse-point attribute, like a real junction.
    /// </summary>
    public void AddJunction(string path, string targetDirectory) => AddReparseDirectory(path, 0xA0000003, targetDirectory);

    /// <summary>
    /// Test helper: adds a directory carrying an arbitrary reparse tag. With a <paramref name="targetDirectory"/> its children are the target's
    /// (a link); without, it lists its own children (like a cloud placeholder folder).
    /// </summary>
    public void AddReparseDirectory(string path, uint reparseTag, string? targetDirectory = null)
    {
        lock (_lock)
        {
            var key = Key(path);
            EnsureDirectories(ParentOf(key));
            var node = NewNode(key, isDirectory: true);
            node.Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;
            node.Tag = reparseTag;
            node.TargetKey = targetDirectory is null ? null : Key(targetDirectory);
            _nodes[key] = node;
        }
    }

    /// <summary>Test helper: lists <paramref name="entry"/> as an extra child of the directory (to simulate a misbehaving file system).</summary>
    public void InjectListingEntry(string directory, FileEntry entry)
    {
        lock (_lock)
        {
            EnsureDirectories(Key(directory));
            Get(directory).Injected.Add(entry);
        }
    }

    /// <summary>Test helper: the directory's listing returns <paramref name="count"/> children and then throws a network error.</summary>
    public void FailListingAfter(string directory, int count)
    {
        lock (_lock)
        {
            EnsureDirectories(Key(directory));
            Get(directory).FailAfter = count;
        }
    }

    /// <summary>Test helper: makes listing the directory fail with <see cref="IoErrorKind.AccessDenied"/> (creates it if missing).</summary>
    public void DenyListing(string path)
    {
        lock (_lock)
        {
            EnsureDirectories(Key(path));
            Get(path).ListingDenied = true;
        }
    }

    /// <summary>Test helper: returns a copy of a file's content.</summary>
    public byte[] ReadAllBytes(string path)
    {
        lock (_lock)
        {
            var node = Get(path);
            return node.IsDirectory ? throw Fail(path, IoErrorKind.AccessDenied) : node.Data.AsSpan(0, node.Length).ToArray();
        }
    }

    public bool Exists(string path)
    {
        lock (_lock)
        {
            return _nodes.ContainsKey(Key(path)) || IsRoot(Key(path));
        }
    }

    public IReadHandle OpenRead(string path, bool unbuffered)
    {
        lock (_lock)
        {
            var node = Get(path);
            return node.IsDirectory ? throw Fail(path, IoErrorKind.AccessDenied) : new ReadHandle(this, node);
        }
    }

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(preallocate);
        lock (_lock)
        {
            var key = Key(path);
            RequireParent(path, key);
            if (_nodes.ContainsKey(key))
            {
                throw Fail(path, IoErrorKind.AlreadyExists); // CreateNew semantics: never truncate or replace
            }

            if (preallocate > FreeSpaceBytes)
            {
                throw Fail(path, IoErrorKind.DiskFull); // same point as the real implementation: reserving space fails at create
            }

            var node = NewNode(key, isDirectory: false);
            _nodes[key] = node;
            return new WriteHandle(this, node, unbuffered, preallocate);
        }
    }

    public FileEntry GetEntry(string path)
    {
        lock (_lock)
        {
            return ToEntry(Get(path));
        }
    }

    public FileEntry GetEntryFollowingLinks(string path)
    {
        lock (_lock)
        {
            var node = Get(path);
            return node.TargetKey is null ? ToEntry(node) : ToEntry(Get(node.TargetKey), node.Key);
        }
    }

    public IEnumerable<FileEntry> EnumerateChildren(string directory)
    {
        lock (_lock)
        {
            var dir = Get(directory);
            if (!dir.IsDirectory)
            {
                throw Fail(directory, IoErrorKind.InvalidName);
            }

            if (dir.ListingDenied)
            {
                throw Fail(directory, IoErrorKind.AccessDenied);
            }

            var requested = Key(directory);
            var shown = requested.EndsWith('\\') ? requested : requested + "\\";
            var source = dir.TargetKey is null ? dir : Get(dir.TargetKey);
            var prefix = source.Key.EndsWith('\\') ? source.Key : source.Key + "\\";
            var list = _nodes.Values
                .Where(n => n.Key.Length > prefix.Length
                    && n.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && n.Key.IndexOf('\\', prefix.Length) < 0)
                .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                .Select(n => ToEntry(n, shown + n.Key[prefix.Length..]))
                .Concat(dir.Injected)
                .ToList();
            return dir.FailAfter is { } after ? FailingListing(list, after, directory) : list;
        }
    }

    private static IEnumerable<FileEntry> FailingListing(List<FileEntry> list, int after, string directory)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (i == after)
            {
                throw Fail(directory, IoErrorKind.NetworkLost);
            }

            yield return list[i];
        }

        if (after >= list.Count)
        {
            throw Fail(directory, IoErrorKind.NetworkLost);
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_lock)
        {
            EnsureDirectories(Key(path));
        }
    }

    public void Move(string source, string destination, bool overwrite)
    {
        lock (_lock)
        {
            var src = Get(source);
            var toKey = Key(destination);
            RequireParent(destination, toKey);
            if (_nodes.TryGetValue(toKey, out var target))
            {
                if (!overwrite || target.IsDirectory || src.IsDirectory)
                {
                    throw Fail(destination, IoErrorKind.AlreadyExists);
                }
            }

            var moved = _nodes.Where(kv => kv.Key.Equals(src.Key, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.StartsWith(src.Key + "\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var kv in moved)
            {
                _nodes.Remove(kv.Key);
            }

            foreach (var kv in moved)
            {
                var newKey = toKey + kv.Key[src.Key.Length..];
                kv.Value.Key = newKey;
                _nodes[newKey] = kv.Value;
            }
        }
    }

    public void Delete(string path)
    {
        lock (_lock)
        {
            var node = Get(path);
            if (node.IsDirectory && EnumerateChildren(path).Any())
            {
                throw Fail(path, IoErrorKind.Other);
            }

            _nodes.Remove(node.Key);
        }
    }

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc)
    {
        lock (_lock)
        {
            var node = Get(path);
            node.Attributes = attributes;
            node.CreationTimeUtc = creationUtc;
            node.LastWriteTimeUtc = lastWriteUtc;
        }
    }

    public long GetFreeSpace(string path) => FreeSpaceBytes;

    public int GetSectorSize(string path) => SectorSize;

    private static string Key(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var k = path.Replace('/', '\\');
        if (k.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            k = @"\\" + k[8..];
        }
        else if (k.StartsWith(LongPrefix, StringComparison.Ordinal))
        {
            k = k[4..];
        }

        while (k.Length > 3 && k.EndsWith('\\'))
        {
            k = k[..^1];
        }

        return k;
    }

    private static string ParentOf(string key)
    {
        var i = key.LastIndexOf('\\');
        return i <= 0 ? string.Empty : key[..(i == 2 && key[1] == ':' ? 3 : i)];
    }

    private static bool IsRoot(string key) => key.Length <= 3 && key.Length >= 2 && key[1] == ':';

    private static FileSystemException Fail(string path, IoErrorKind kind) => new(path, kind, null);

    private Node NewNode(string key, bool isDirectory)
    {
        var now = Clock();
        return new Node
        {
            Key = key,
            IsDirectory = isDirectory,
            FileId = new UInt128(0x1234, _nextId++),
            VolumeSerial = 0xE0E0_0000_0000UL + (key.Length > 0 ? (ulong)(char.ToUpperInvariant(key[0]) - 'A') : 0UL),
            Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            CreationTimeUtc = now,
            LastWriteTimeUtc = now,
        };
    }

    private Node Get(string path, int depth = 0)
    {
        var key = Key(path);
        if (_nodes.TryGetValue(key, out var node))
        {
            return node;
        }

        if (IsRoot(key))
        {
            node = NewNode(key.EndsWith('\\') ? key : key + "\\", isDirectory: true);
            _nodes[key] = node;
            return node;
        }

        // A path below a junction resolves to the same object below the junction's target.
        var previous = key;
        for (var parent = ParentOf(key); parent.Length > 0 && parent.Length < previous.Length; previous = parent, parent = ParentOf(parent))
        {
            if (_nodes.TryGetValue(parent, out var link) && link.TargetKey is not null)
            {
                return depth < 40 ? Get(link.TargetKey + key[parent.Length..], depth + 1) : throw Fail(path, IoErrorKind.NotFound);
            }
        }

        throw Fail(path, IoErrorKind.NotFound);
    }

    private void RequireParent(string path, string key)
    {
        var parent = ParentOf(key);
        if (parent.Length == 0)
        {
            return;
        }

        if (!_nodes.TryGetValue(parent, out var p) && !IsRoot(parent))
        {
            throw Fail(path, IoErrorKind.NotFound);
        }

        if (p is { IsDirectory: false })
        {
            throw Fail(path, IoErrorKind.InvalidName);
        }
    }

    private void EnsureDirectories(string key)
    {
        if (key.Length == 0 || IsRoot(key))
        {
            return;
        }

        if (_nodes.TryGetValue(key, out var existing))
        {
            if (!existing.IsDirectory)
            {
                throw Fail(key, IoErrorKind.InvalidName);
            }

            return;
        }

        EnsureDirectories(ParentOf(key));
        _nodes[key] = NewNode(key, isDirectory: true);
    }

    private static FileEntry ToEntry(Node n) => ToEntry(n, n.Key);

    private static FileEntry ToEntry(Node n, string key) => new(
        key.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + key[2..] : LongPrefix + key,
        n.IsDirectory ? 0 : n.Length,
        n.Attributes,
        n.CreationTimeUtc,
        n.LastWriteTimeUtc,
        n.Attributes.HasFlag(FileAttributes.ReparsePoint),
        n.FileId,
        n.VolumeSerial,
        n.Tag);

    private sealed class Node
    {
        public string Key = string.Empty;
        public bool IsDirectory;
        public byte[] Data = [];
        public int Length;
        public FileAttributes Attributes;
        public DateTime CreationTimeUtc;
        public DateTime LastWriteTimeUtc;
        public UInt128 FileId;
        public ulong VolumeSerial;
        public uint Tag;
        public List<FileEntry> Injected = [];
        public int? FailAfter;
        public string? TargetKey;
        public bool ListingDenied;
    }

    private sealed class ReadHandle(InMemoryFileSystem fs, Node node) : IReadHandle
    {
        public long Length
        {
            get
            {
                lock (fs._lock)
                {
                    return node.Length;
                }
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (fs._lock)
            {
                if (offset >= node.Length)
                {
                    return ValueTask.FromResult(0);
                }

                var n = (int)Math.Min(buffer.Length, node.Length - offset);
                node.Data.AsSpan((int)offset, n).CopyTo(buffer.Span);
                return ValueTask.FromResult(n);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WriteHandle : IWriteHandle
    {
        private readonly InMemoryFileSystem fs;
        private readonly Node node;
        private readonly bool unbuffered;
        private bool _disposed;
        private int _logical; // end of the data written so far
        private bool _needsTrim;

        public WriteHandle(InMemoryFileSystem fs, Node node, bool unbuffered, long preallocate)
        {
            this.fs = fs;
            this.node = node;
            this.unbuffered = unbuffered;
            if (preallocate > 0)
            {
                // End of file is set up front, like the real implementation; trimmed to the data written on flush/dispose.
                var len = checked((int)preallocate);
                Grow(len);
                node.Length = len;
                _needsTrim = true;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ct.ThrowIfCancellationRequested();
            if (buffer.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            lock (fs._lock)
            {
                if (unbuffered)
                {
                    // Same rules and exception types as the real implementation.
                    if (offset % fs.SectorSize != 0)
                    {
                        throw new ArgumentException("Unbuffered write offset must be sector aligned.", nameof(offset));
                    }

                    if (buffer.Length % fs.SectorSize != 0 && offset + buffer.Length < _logical)
                    {
                        throw new InvalidOperationException("A short unbuffered write must not end before data that already exists.");
                    }
                }

                var end = checked((int)(offset + buffer.Length));
                Grow(end);
                buffer.Span.CopyTo(node.Data.AsSpan((int)offset));
                node.Length = Math.Max(node.Length, end);
                _logical = Math.Max(_logical, end);
                node.LastWriteTimeUtc = fs.Clock();
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask SetLengthAsync(long length)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (fs._lock)
            {
                var len = checked((int)length);
                Grow(len);
                if (len < node.Length)
                {
                    node.Data.AsSpan(len, node.Length - len).Clear();
                }

                node.Length = len;
                _logical = len;
                _needsTrim = false;
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask FlushAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Trim();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                Trim();
                _disposed = true;
            }

            return ValueTask.CompletedTask;
        }

        private void Trim()
        {
            lock (fs._lock)
            {
                if (_needsTrim && node.Length > _logical)
                {
                    node.Data.AsSpan(_logical, node.Length - _logical).Clear();
                    node.Length = _logical;
                }

                _needsTrim = false;
            }
        }

        private void Grow(int size)
        {
            if (node.Data.Length < size)
            {
                Array.Resize(ref node.Data, Math.Max(size, node.Data.Length * 2));
            }
        }
    }
}
