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
    public void AddFile(string path, byte[] content)
    {
        lock (_lock)
        {
            var key = Key(path);
            EnsureDirectories(ParentOf(key));
            var node = NewNode(key, isDirectory: false);
            node.Data = content.ToArray();
            node.Length = content.Length;
            _nodes[key] = node;
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
            return new WriteHandle(this, node, unbuffered);
        }
    }

    public FileEntry GetEntry(string path)
    {
        lock (_lock)
        {
            return ToEntry(Get(path));
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

            var prefix = dir.Key.EndsWith('\\') ? dir.Key : dir.Key + "\\";
            return _nodes.Values
                .Where(n => n.Key.Length > prefix.Length
                    && n.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && n.Key.IndexOf('\\', prefix.Length) < 0)
                .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                .Select(ToEntry)
                .ToList();
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
            FileId = _nextId++,
            VolumeSerial = 0xE0E0_0000u + (key.Length > 0 ? (uint)(char.ToUpperInvariant(key[0]) - 'A') : 0u),
            Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            CreationTimeUtc = now,
            LastWriteTimeUtc = now,
        };
    }

    private Node Get(string path)
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

    private FileEntry ToEntry(Node n) => new(
        n.Key,
        n.IsDirectory ? 0 : n.Length,
        n.Attributes,
        n.CreationTimeUtc,
        n.LastWriteTimeUtc,
        n.Attributes.HasFlag(FileAttributes.ReparsePoint),
        n.FileId,
        n.VolumeSerial);

    private sealed class Node
    {
        public string Key = string.Empty;
        public bool IsDirectory;
        public byte[] Data = [];
        public int Length;
        public FileAttributes Attributes;
        public DateTime CreationTimeUtc;
        public DateTime LastWriteTimeUtc;
        public ulong FileId;
        public uint VolumeSerial;
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

    private sealed class WriteHandle(InMemoryFileSystem fs, Node node, bool unbuffered) : IWriteHandle
    {
        private bool _disposed;

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

                    if (buffer.Length % fs.SectorSize != 0 && offset + buffer.Length < node.Length)
                    {
                        throw new InvalidOperationException("A short unbuffered write must not end before data that already exists.");
                    }
                }

                var end = checked((int)(offset + buffer.Length));
                Grow(end);
                buffer.Span.CopyTo(node.Data.AsSpan((int)offset));
                node.Length = Math.Max(node.Length, end);
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
                return ValueTask.CompletedTask;
            }
        }

        public ValueTask FlushAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
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
