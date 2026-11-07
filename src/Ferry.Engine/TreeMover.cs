using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>What a same-drive rename did.</summary>
internal enum MoveOutcome
{
    Moved,
    Failed,

    /// <summary>Windows said the two places are on different volumes; nothing changed and the caller must copy and verify instead.</summary>
    NotSameDevice,
}

/// <summary>
/// Same-drive moves: renames only (never a copy: <see cref="IFileSystem.Move"/> refuses to cross volumes), so data never changes and needs no
/// verify. Whole folders move in one rename when nothing is in the way; links (to folders or files) are moved as the links themselves, never
/// followed, and left in place when the move crosses drives.
/// </summary>
internal sealed class TreeMover(
    IFileSystem fs, string destination, JobOutcome outcome, JobJournalWriter journal, ProgressTracker progress, Action<TreeItem, Problem> fail)
{
    /// <summary>One rename for a whole folder. Its files then count as moved. NotSameDevice leaves everything as it was.</summary>
    public MoveOutcome MoveWhole(string source, string key, string journalKey, int files, long bytes)
    {
        try
        {
            fs.Move(source, Path.Combine(destination, key), overwrite: false);
            journal.Done(journalKey, 0, DateTime.UtcNow, null);
            outcome.Copied += files;
        }
        catch (FileSystemException ex) when (ex.Kind == IoErrorKind.NotSameDevice)
        {
            return MoveOutcome.NotSameDevice;
        }
        catch (FileSystemException ex)
        {
            outcome.Failed += files;
            outcome.Problems.Add(new Problem(TreePaths.Display(source), ex.Kind, WriteMessage(ex.Kind)));
            progress.CompleteFiles(files, bytes);
            return MoveOutcome.Failed;
        }

        progress.CompleteFiles(files, bytes);
        return MoveOutcome.Moved;
    }

    /// <summary>A link inside a move: renamed as the link itself on the same drive, left in place (and listed) otherwise.</summary>
    public void MoveLink(bool sameVolume, TreeItem item)
    {
        var isFolder = item.Entry.Attributes.HasFlag(FileAttributes.Directory);
        var leftInPlace = isFolder ? CopyMessages.LinkNotMovedAcrossDrives : CopyMessages.FileLinkSkipped;
        if (!sameVolume)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), IoErrorKind.None, leftInPlace));
            return;
        }

        var target = Path.Combine(destination, item.Key);
        try
        {
            if (TreePaths.TryGet(fs, target, follow: false) is not null)
            {
                throw new FileSystemException(target, IoErrorKind.AlreadyExists, null);
            }

            fs.Move(item.Entry.FullPath, target, overwrite: false);
        }
        catch (FileSystemException ex) when (ex.Kind == IoErrorKind.NotSameDevice)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), IoErrorKind.None, leftInPlace));
        }
        catch (FileSystemException ex)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), ex.Kind, WriteMessage(ex.Kind)));
        }
    }

    /// <summary>
    /// Same-drive move of one file to <paramref name="target"/>, replacing an existing file when <paramref name="replace"/> (ReadOnly cleared
    /// first). An existing file is otherwise never touched (KeepBoth and Rename give the incoming file a new name instead).
    /// </summary>
    public MoveOutcome MoveFile(TreeItem item, string target, bool replace)
    {
        var source = item.Entry.FullPath;
        try
        {
            if (replace)
            {
                var old = fs.GetEntry(target);
                if (old.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    fs.SetMetadata(target, old.Attributes & ~FileAttributes.ReadOnly, old.CreationTimeUtc, old.LastWriteTimeUtc);
                }

                try
                {
                    fs.Move(source, target, overwrite: true);
                }
                catch (FileSystemException)
                {
                    TrySetMetadata(target, old);
                    throw;
                }
            }
            else
            {
                fs.Move(source, target, overwrite: false);
            }

            outcome.Copied++;
            journal.Done(item.JournalKey, item.Entry.Length, item.Entry.LastWriteTimeUtc, null);
            return MoveOutcome.Moved;
        }
        catch (FileSystemException ex) when (ex.Kind == IoErrorKind.NotSameDevice)
        {
            return MoveOutcome.NotSameDevice;
        }
        catch (FileSystemException ex)
        {
            fail(item, new Problem(TreePaths.Display(source), ex.Kind, WriteMessage(ex.Kind)));
            return MoveOutcome.Failed;
        }
    }

    private void TrySetMetadata(string path, FileEntry entry)
    {
        try
        {
            fs.SetMetadata(path, entry.Attributes, entry.CreationTimeUtc, entry.LastWriteTimeUtc);
        }
        catch (FileSystemException)
        {
            // Best effort: only the ReadOnly flag may be off.
        }
    }

    private string WriteMessage(IoErrorKind kind) => CopyMessages.For(kind, whileWriting: true, CopyMessages.DriveOf(destination));
}
