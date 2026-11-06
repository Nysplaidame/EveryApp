using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Tests.Hashing;

/// <summary>Wraps a file system so reads return at most <c>maxPerRead</c> bytes and are counted.</summary>
internal sealed class TrickleFileSystem(IFileSystem inner, int maxPerRead) : IFileSystem
{
    public int Reads { get; set; }

    public int MaxPerRead { get; } = maxPerRead;

    public IReadHandle OpenRead(string path, bool unbuffered) => new Handle(this, inner.OpenRead(path, unbuffered));

    public IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered) => inner.CreateWrite(path, preallocate, unbuffered);

    public FileEntry GetEntry(string path) => inner.GetEntry(path);

    public FileEntry GetEntryFollowingLinks(string path) => inner.GetEntryFollowingLinks(path);

    public IEnumerable<FileEntry> EnumerateChildren(string directory) => inner.EnumerateChildren(directory);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string destination, bool overwrite) => inner.Move(source, destination, overwrite);

    public void Delete(string path) => inner.Delete(path);

    public void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc)
        => inner.SetMetadata(path, attributes, creationUtc, lastWriteUtc);

    public long GetFreeSpace(string path) => inner.GetFreeSpace(path);

    public int GetSectorSize(string path) => inner.GetSectorSize(path);

    private sealed class Handle(TrickleFileSystem owner, IReadHandle h) : IReadHandle
    {
        public long Length => h.Length;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct)
        {
            owner.Reads++;
            return h.ReadAsync(buffer.Length > owner.MaxPerRead ? buffer[..owner.MaxPerRead] : buffer, offset, ct);
        }

        public ValueTask DisposeAsync() => h.DisposeAsync();
    }
}
