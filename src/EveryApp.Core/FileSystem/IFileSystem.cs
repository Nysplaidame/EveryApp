namespace EveryApp.Core.FileSystem;

/// <summary>Random-access read handle. Offsets are absolute; reads may return fewer bytes than asked (0 at end of file).</summary>
public interface IReadHandle : IAsyncDisposable
{
    long Length { get; }

    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken ct);
}

/// <summary>
/// Random-access write handle. On an unbuffered handle every write offset must be a multiple of the volume sector size;
/// only the last write may be shorter than a whole number of sectors (a short write that would end before existing data throws
/// InvalidOperationException). The file ends up exactly as long as the data written. Use after dispose throws ObjectDisposedException,
/// and dispose waits for in-flight operations.
/// </summary>
public interface IWriteHandle : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct);

    ValueTask SetLengthAsync(long length);

    ValueTask FlushAsync();
}

/// <summary>
/// All disk access in the engine goes through this interface so failures can be injected in tests.
/// Every failure surfaces as a <see cref="FileSystemException"/> (the original exception is kept as inner exception).
/// </summary>
public interface IFileSystem
{
    IReadHandle OpenRead(string path, bool unbuffered);

    /// <summary>
    /// Creates a NEW file; throws <see cref="IoErrorKind.AlreadyExists"/> if the path is taken (an existing file is never truncated).
    /// When <paramref name="preallocate"/> is above zero disk space is reserved up front so a full disk fails early; the file length stays 0
    /// until data is written. Callers write to a temporary name, call FlushAsync, then Move it into place, so a crash never leaves a
    /// half-written file under the final name.
    /// </summary>
    IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered);

    FileEntry GetEntry(string path);

    IEnumerable<FileEntry> EnumerateChildren(string directory);

    void CreateDirectory(string path);

    void Move(string source, string destination, bool overwrite);

    /// <summary>Deletes a file or an empty directory (a junction or symlink is removed itself, never followed). Throws NotFound when missing.</summary>
    void Delete(string path);

    void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc);

    long GetFreeSpace(string path);

    int GetSectorSize(string path);
}
