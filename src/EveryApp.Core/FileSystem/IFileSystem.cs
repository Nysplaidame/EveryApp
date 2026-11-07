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
    /// When <paramref name="preallocate"/> is above zero the end of file is set to that length up front, so a full disk fails early and
    /// extending writes do not serialise; the file is trimmed to the data actually written on FlushAsync and DisposeAsync.
    /// The target MUST be a temporary part-file name: until the copy is verified the content is untrusted (it may be full-size but only
    /// partly written after a crash). Write, FlushAsync, verify, then Move it into place.
    /// </summary>
    IWriteHandle CreateWrite(string path, long preallocate, bool unbuffered);

    FileEntry GetEntry(string path);

    /// <summary>
    /// Like <see cref="GetEntry"/> but for a link (junction, symlink, mount point) describes the TARGET it leads to, not the link itself
    /// (FullPath stays the path given). Use it to learn which folder a link really reaches, e.g. for loop and duplicate-path detection.
    /// </summary>
    FileEntry GetEntryFollowingLinks(string path);

    IEnumerable<FileEntry> EnumerateChildren(string directory);

    void CreateDirectory(string path);

    void Move(string source, string destination, bool overwrite);

    /// <summary>Deletes a file or an empty directory (a junction or symlink is removed itself, never followed). Throws NotFound when missing.</summary>
    void Delete(string path);

    void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc);

    long GetFreeSpace(string path);

    int GetSectorSize(string path);

    /// <summary>
    /// Gives <paramref name="target"/> the permissions of <paramref name="source"/>: its explicit access entries, whether it blocks inheritance,
    /// and its owner when that differs. Entries inherited from the new parent folder stay as Windows computes them. Throws
    /// <see cref="IoErrorKind.AccessDenied"/> when this needs rights the user does not have (for example setting another owner).
    /// </summary>
    void CopySecurity(string source, string target);

    /// <summary>
    /// Copies every extra named data stream (alternate data stream) of the file <paramref name="source"/> onto the file <paramref name="target"/>.
    /// <paramref name="target"/> must be a file this job created (a part file): an existing stream of the same name on it is replaced.
    /// </summary>
    void CopyAlternateStreams(string source, string target);

    /// <summary>
    /// Creates the NEW file <paramref name="target"/> sharing the data blocks of <paramref name="source"/> (block cloning: ReFS, same volume only).
    /// Returns false, leaving nothing behind, when cloning is not possible for any reason (other file system, other volume, empty file,
    /// <paramref name="target"/> already exists, any error); the caller then copies normally. Never throws for those cases.
    /// </summary>
    bool TryCloneBlocks(string source, string target);
}
