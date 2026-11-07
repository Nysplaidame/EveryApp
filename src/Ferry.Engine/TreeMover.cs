using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>
/// Same-drive moves: renames only, so data never changes and needs no verify. Whole folders move in one rename when nothing is in the
/// way; links are moved as the links themselves (never followed) and left in place when the move crosses drives.
/// </summary>
internal sealed class TreeMover(
    IFileSystem fs, string destination, JobOutcome outcome, JobJournalWriter journal, ProgressTracker progress, Action<TreeItem, Problem> fail)
{
    /// <summary>One rename for a whole folder. Its files then count as moved.</summary>
    public void MoveWhole(string source, string key, int files, long bytes)
    {
        try
        {
            fs.Move(source, Path.Combine(destination, key), overwrite: false);
            journal.Done(key, 0, DateTime.UtcNow, null);
            outcome.Copied += files;
        }
        catch (FileSystemException ex)
        {
            outcome.Failed += files;
            outcome.Problems.Add(new Problem(TreePaths.Display(source), ex.Kind, WriteMessage(ex.Kind)));
        }

        progress.CompleteFiles(files, bytes);
    }

    /// <summary>A folder link inside a move: renamed as the link itself on the same drive, left in place otherwise.</summary>
    public void MoveLink(TreeRoot root, TreeItem item)
    {
        if (!root.SameVolume)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), IoErrorKind.None, CopyMessages.LinkNotMovedAcrossDrives));
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
        catch (FileSystemException ex)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(item.Entry.FullPath), ex.Kind, WriteMessage(ex.Kind)));
        }
    }

    /// <summary>Same-drive move of one file to <paramref name="target"/>, replacing an existing file when <paramref name="replace"/> (ReadOnly cleared first).</summary>
    public void MoveFile(TreeItem item, string target, bool replace, string? keepOldAs)
    {
        var source = item.Entry.FullPath;
        try
        {
            if (keepOldAs is not null)
            {
                fs.Move(target, keepOldAs, overwrite: false);
                try
                {
                    fs.Move(source, target, overwrite: false);
                }
                catch (FileSystemException)
                {
                    TryMoveBack(keepOldAs, target);
                    throw;
                }
            }
            else if (replace)
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
            journal.Done(item.Key, item.Entry.Length, item.Entry.LastWriteTimeUtc, null);
        }
        catch (FileSystemException ex)
        {
            fail(item, new Problem(TreePaths.Display(source), ex.Kind, WriteMessage(ex.Kind)));
        }
    }

    /// <summary>Puts a file back where it was; a failure is listed (nothing is lost, only the name is different).</summary>
    public void TryMoveBack(string from, string to)
    {
        try
        {
            fs.Move(from, to, overwrite: false);
        }
        catch (FileSystemException ex)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(from), ex.Kind, WriteMessage(ex.Kind)));
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
