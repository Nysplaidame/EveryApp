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

    /// <summary>Where this open file really is, as Windows resolved it (every link followed), in the canonical <c>\\?\</c> form.</summary>
    string GetFinalPath();

    /// <summary>
    /// Deletes the file through this handle when it closes (the handle carries delete access). The name is never looked up again, so a link
    /// swapped in meanwhile cannot redirect the delete.
    /// </summary>
    void DeleteOnClose();
}

/// <summary>How <see cref="IFileSystem.CopySecurity"/> went. Every outcome leaves the file readable and changeable by the current user.</summary>
public enum SecurityCopyResult
{
    /// <summary>The (filtered) permissions and the owner were copied.</summary>
    Copied,

    /// <summary>The (filtered) permissions were copied, but the owner could not be set (that needs rights the user does not have).</summary>
    OwnerNotKept,

    /// <summary>The copied permissions would have locked the user out, so the file was put back to the permissions its folder passes down.</summary>
    Reverted,
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

    /// <summary>
    /// Renames a file or folder (a link is renamed itself, never followed). Never copies: across volumes it throws
    /// <see cref="IoErrorKind.NotSameDevice"/> and changes nothing.
    /// </summary>
    void Move(string source, string destination, bool overwrite);

    /// <summary>Deletes a file or an empty directory (a junction or symlink is removed itself, never followed). Throws NotFound when missing.</summary>
    void Delete(string path);

    void SetMetadata(string path, FileAttributes attributes, DateTime creationUtc, DateTime lastWriteUtc);

    long GetFreeSpace(string path);

    int GetSectorSize(string path);

    /// <summary>
    /// Gives <paramref name="target"/> the permissions of <paramref name="source"/>, filtered so they can never be wider than safe: explicit
    /// entries for unknown or foreign accounts, OWNER RIGHTS, and allow entries that let Everyone, Users, Authenticated Users or Anonymous
    /// change the item are left out. Owner and access list are written in one step (never half applied); if the owner cannot be set the
    /// access list alone is written. If the result would lock the current user out, the item is put back to inherited permissions.
    /// </summary>
    SecurityCopyResult CopySecurity(string source, string target);

    /// <summary>
    /// Copies named data streams of the file <paramref name="source"/> onto the file <paramref name="target"/> (a part file this job created;
    /// a stream of the same name on it is replaced). With <paramref name="allStreams"/> false only the "downloaded from the internet" marker
    /// (<c>Zone.Identifier</c>) is copied. Only names of the form <c>:name:$DATA</c> are accepted; anything else is ignored.
    /// </summary>
    void CopyAlternateStreams(string source, string target, bool allStreams, CancellationToken ct);

    /// <summary>Where the item really is (every link followed), in the canonical <c>\\?\</c> form. Throws when it does not exist.</summary>
    string GetFinalPath(string path);

    /// <summary>
    /// The volume GUID path (<c>\\?\Volume{...}\</c>) of the volume holding <paramref name="path"/> (which need not exist yet), or null when
    /// it cannot be told (network share, SUBST drive): callers must then treat it as another volume.
    /// </summary>
    string? GetVolumeId(string path);

    /// <summary>
    /// Deletes a file only if it is still the one expected: opened without following links, its id, volume, length and last-write time must
    /// match, and its final path must be under <paramref name="mustBeUnder"/> (when given). Deletes through that same handle. Returns false,
    /// deleting nothing, on any mismatch; throws for I/O failures.
    /// </summary>
    bool DeleteVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, long expectedLength, DateTime expectedLastWriteUtc, string? mustBeUnder);

    /// <summary>
    /// Deletes a folder only if it is empty and still the one expected (id and volume, under <paramref name="mustBeUnder"/>), through the
    /// handle that checked it. A link is removed itself, never followed. Returns false, deleting nothing, otherwise.
    /// </summary>
    bool DeleteEmptyFolderVerified(string path, UInt128 expectedFileId, ulong expectedVolumeSerial, string? mustBeUnder);

    /// <summary>
    /// Creates the NEW file <paramref name="target"/> sharing the data blocks of <paramref name="source"/> (block cloning: ReFS, same volume only).
    /// Returns false, leaving nothing behind, when cloning is not possible for any reason (other file system, other volume, empty file,
    /// <paramref name="target"/> already exists, any error); the caller then copies normally. Never throws for those cases.
    /// </summary>
    bool TryCloneBlocks(string source, string target);
}
