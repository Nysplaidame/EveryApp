using EveryApp.Core.FileSystem;
using Microsoft.Extensions.FileSystemGlobbing;

namespace EveryApp.Core.Paths;

/// <summary>One file or folder found by <see cref="TreeWalker"/>. <paramref name="RelativePath"/> uses backslashes and never starts with one.</summary>
public sealed record WalkItem(FileEntry Entry, string RelativePath);

/// <summary>Something the walk could not or would not enter. <paramref name="Path"/> and <paramref name="Reason"/> are fit to show to people.</summary>
public sealed record WalkSkip(string Path, string Reason);

/// <summary>
/// Lazy, iterative (explicit stack, so depth is limited by memory not the call stack) walk of one folder tree.
/// It never leaves the root (links are not followed unless asked), never loops (when following links, every folder is entered at most once,
/// keyed on volume serial and file id) and never stops on one bad folder (the problem is reported and the walk goes on).
/// Folders are listed before their contents. Only one directory handle is open at a time.
/// </summary>
public sealed class TreeWalker
{
    private const string DirectoryProbe = "/_";

    private readonly IFileSystem _fs;
    private readonly bool _followReparsePoints;
    private readonly Matcher? _exclude;

    /// <param name="fs">Disk access.</param>
    /// <param name="followReparsePoints">False (the default choice) skips every link, junction and other reparse point and reports it.</param>
    /// <param name="excludeGlobs">
    /// Patterns matched against the path relative to the root, case-insensitively, with <c>/</c> separators (<c>\</c> in a pattern is accepted).
    /// A pattern without a slash matches at any depth (<c>Thumbs.db</c> means <c>**/Thumbs.db</c>). A matching folder is not entered.
    /// Excluded items are left out silently: that is a choice, not a problem.
    /// </param>
    public TreeWalker(IFileSystem fs, bool followReparsePoints, IReadOnlyList<string> excludeGlobs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(excludeGlobs);
        _fs = fs;
        _followReparsePoints = followReparsePoints;

        if (excludeGlobs.Count > 0)
        {
            _exclude = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var glob in excludeGlobs)
            {
                var pattern = glob.Trim().Replace('\\', '/');
                if (pattern.Length == 0)
                {
                    continue;
                }

                _exclude.AddInclude(pattern.Contains('/', StringComparison.Ordinal) ? pattern : "**/" + pattern);
            }
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/>. Relative paths are computed from the canonical root, never from the caller's text. The root itself is always
    /// entered, even if it is a link (the person chose it). Problems go to <paramref name="onSkip"/> as they happen.
    /// </summary>
    public IEnumerable<WalkItem> Walk(string root, Action<WalkSkip> onSkip)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(onSkip);
        return WalkCore(root, onSkip);
    }

    private IEnumerable<WalkItem> WalkCore(string root, Action<WalkSkip> onSkip)
    {
        FileEntry rootEntry;
        try
        {
            rootEntry = _fs.GetEntry(root);
        }
        catch (FileSystemException ex)
        {
            onSkip(new WalkSkip(root, Describe(ex.Kind, "folder")));
            yield break;
        }

        if (!rootEntry.Attributes.HasFlag(FileAttributes.Directory))
        {
            onSkip(new WalkSkip(PathDisplay.ToDisplayPath(rootEntry.FullPath), "This is a file, not a folder."));
            yield break;
        }

        var rootPath = rootEntry.FullPath;
        var prefixLength = rootPath.EndsWith('\\') ? rootPath.Length : rootPath.Length + 1;

        HashSet<(ulong, UInt128)>? visited = null;
        if (_followReparsePoints)
        {
            visited = [(rootEntry.VolumeSerial, rootEntry.FileId)];
        }

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerator<FileEntry> children;
            try
            {
                children = _fs.EnumerateChildren(directory).GetEnumerator();
            }
            catch (FileSystemException ex)
            {
                onSkip(new WalkSkip(PathDisplay.ToDisplayPath(directory), Describe(ex.Kind, "folder")));
                continue;
            }

            using (children)
            {
                while (true)
                {
                    FileEntry child;
                    try
                    {
                        if (!children.MoveNext())
                        {
                            break;
                        }

                        child = children.Current;
                    }
                    catch (FileSystemException ex)
                    {
                        // The listing broke part-way (the folder went away, the drive dropped). Report it and move on; what was
                        // already returned stays valid.
                        onSkip(new WalkSkip(PathDisplay.ToDisplayPath(directory), Describe(ex.Kind, "folder")));
                        break;
                    }

                    var full = child.FullPath;
                    if (full.Length <= prefixLength || !full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cannot happen with a well-behaved file system; never hand out a path that is not under the root.
                        onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "This item is outside the chosen folder."));
                        continue;
                    }

                    var relative = full.Substring(prefixLength);
                    var isDirectory = child.Attributes.HasFlag(FileAttributes.Directory);

                    if (_exclude is not null && IsExcluded(relative, isDirectory))
                    {
                        continue;
                    }

                    if (child.IsReparsePoint)
                    {
                        if (!_followReparsePoints)
                        {
                            onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "Skipped: this is a link to somewhere else."));
                            continue;
                        }

                        if (isDirectory && !visited!.Add((child.VolumeSerial, child.FileId)))
                        {
                            onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "Skipped: this link leads back to a folder already visited, which would loop forever."));
                            continue;
                        }
                    }
                    else if (isDirectory)
                    {
                        visited?.Add((child.VolumeSerial, child.FileId));
                    }

                    yield return new WalkItem(child, relative);

                    if (isDirectory)
                    {
                        pending.Push(full);
                    }
                }
            }
        }
    }

    private bool IsExcluded(string relative, bool isDirectory)
    {
        var slashed = relative.Replace('\\', '/');
        if (_exclude!.Match(slashed).HasMatches)
        {
            return true;
        }

        // A folder-only pattern such as "**/node_modules/**" matches what is inside the folder, so probe with a child name to prune the folder itself.
        return isDirectory && _exclude!.Match(slashed + DirectoryProbe).HasMatches;
    }

    private static string Describe(IoErrorKind kind, string what) => kind switch
    {
        IoErrorKind.AccessDenied => $"Skipped: you don't have permission to open this {what}.",
        IoErrorKind.NotFound => $"Skipped: this {what} could not be found (it may have been moved or deleted).",
        IoErrorKind.SharingViolation => $"Skipped: another program is using this {what}.",
        IoErrorKind.NetworkLost => $"Skipped: the connection to this {what} was lost.",
        IoErrorKind.PathTooLong => $"Skipped: the path to this {what} is too long.",
        _ => $"Skipped: this {what} could not be read.",
    };
}
