using EveryApp.Core.FileSystem;
using Microsoft.Extensions.FileSystemGlobbing;

namespace EveryApp.Core.Paths;

/// <summary>One file or folder found by <see cref="TreeWalker"/>. <paramref name="RelativePath"/> uses backslashes and never starts with one.</summary>
public sealed record WalkItem(FileEntry Entry, string RelativePath);

/// <summary>Something the walk could not or would not enter. <paramref name="Path"/> and <paramref name="Reason"/> are fit to show to people.</summary>
public sealed record WalkSkip(string Path, string Reason);

/// <summary>
/// Lazy, iterative (explicit stack, so depth is limited by memory not the call stack) walk of one folder tree.
/// It never leaves the root (links to folders are not followed unless asked), never loops and never lists one physical folder twice
/// (when following links, every folder is entered at most once, keyed on the volume serial and file id of the folder it really is),
/// and never stops on one bad folder (the problem is reported and the walk goes on).
/// Folders are listed before their contents. Only one directory handle is open at a time.
/// </summary>
/// <remarks>
/// Only name-surrogate reparse points (junctions, symlinks, mount points) count as links. Other reparse folders, such as cloud placeholder
/// folders, are ordinary user data and are walked. Files are always listed whatever their reparse tag; the tag is in the entry so the caller
/// can decide how to treat a file symlink.
/// </remarks>
public sealed class TreeWalker
{
    private const uint NameSurrogateBit = 0x20000000;

    private readonly IFileSystem _fs;
    private readonly bool _followReparsePoints;
    private readonly Matcher? _exclude;
    private readonly Matcher? _excludeFolders;

    /// <param name="fs">Disk access.</param>
    /// <param name="followReparsePoints">False (the default choice) skips every folder link (junction, symlink, mount point) and reports it.</param>
    /// <param name="excludeGlobs">
    /// Patterns matched against the path relative to the root, case-insensitively, with <c>/</c> separators (<c>\</c> in a pattern is accepted).
    /// A pattern without a slash matches at any depth (<c>Thumbs.db</c> means <c>**/Thumbs.db</c>). An item is excluded when its own relative path
    /// matches. A folder is also excluded, with everything in it, when a pattern ending in <c>/**</c> matches the folder's own path once that
    /// suffix is removed (<c>**/node_modules/**</c> prunes every folder named node_modules). Excluded items are left out silently: that is a
    /// choice, not a problem.
    /// </param>
    public TreeWalker(IFileSystem fs, bool followReparsePoints, IReadOnlyList<string> excludeGlobs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(excludeGlobs);
        _fs = fs;
        _followReparsePoints = followReparsePoints;

        foreach (var glob in excludeGlobs)
        {
            var pattern = glob.Trim().Replace('\\', '/');
            if (pattern.Length == 0)
            {
                continue;
            }

            if (!pattern.Contains('/', StringComparison.Ordinal))
            {
                pattern = "**/" + pattern;
            }

            _exclude ??= new Matcher(StringComparison.OrdinalIgnoreCase);
            _exclude.AddInclude(pattern);

            if (pattern.EndsWith("/**", StringComparison.Ordinal) && pattern.Length > 3)
            {
                _excludeFolders ??= new Matcher(StringComparison.OrdinalIgnoreCase);
                _excludeFolders.AddInclude(pattern[..^3]);
            }
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/>. Relative paths are computed from the canonical root, never from the caller's text. The root itself is always
    /// entered, even if it is a link (the person chose it). Problems go to <paramref name="onSkip"/> as they happen.
    /// </summary>
    public IEnumerable<WalkItem> Walk(string root, Action<WalkSkip> onSkip) => Walk(root, onSkip, null);

    /// <summary>
    /// As <see cref="Walk(string, Action{WalkSkip})"/>, but a folder link that is not followed goes to <paramref name="onLink"/> (as the link
    /// itself, never entered) instead of being reported as skipped. A move uses this to move links as links.
    /// </summary>
    public IEnumerable<WalkItem> Walk(string root, Action<WalkSkip> onSkip, Action<WalkItem>? onLink)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(onSkip);
        return WalkCore(root, onSkip, onLink);
    }

    private static bool IsNameSurrogateFolder(FileEntry entry) =>
        entry.IsReparsePoint && entry.Attributes.HasFlag(FileAttributes.Directory) && (entry.ReparseTag & NameSurrogateBit) != 0;

    private IEnumerable<WalkItem> WalkCore(string root, Action<WalkSkip> onSkip, Action<WalkItem>? onLink)
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
        var rootPrefix = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";

        // Which physical folders have been entered. Only needed when links can be followed: without that, every folder is reached by exactly one path.
        HashSet<(ulong, UInt128)>? visited = null;
        if (_followReparsePoints)
        {
            FileEntry rootTarget;
            try
            {
                rootTarget = IsNameSurrogateFolder(rootEntry) ? _fs.GetEntryFollowingLinks(rootPath) : rootEntry;
            }
            catch (FileSystemException ex)
            {
                onSkip(new WalkSkip(PathDisplay.ToDisplayPath(rootPath), Describe(ex.Kind, "folder")));
                yield break;
            }

            visited = [(rootTarget.VolumeSerial, rootTarget.FileId)];
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
                    if (full.Length <= rootPrefix.Length || !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cannot happen with a well-behaved file system; never hand out a path that is not under the root.
                        onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "This item is outside the chosen folder."));
                        continue;
                    }

                    var relative = full.Substring(rootPrefix.Length);
                    var isDirectory = child.Attributes.HasFlag(FileAttributes.Directory);

                    if (_exclude is not null && IsExcluded(relative, isDirectory))
                    {
                        continue;
                    }

                    if (isDirectory)
                    {
                        if (IsNameSurrogateFolder(child) && !_followReparsePoints)
                        {
                            if (onLink is not null)
                            {
                                onLink(new WalkItem(child, relative));
                                continue;
                            }

                            onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "Skipped: this is a link to another folder."));
                            continue;
                        }

                        if (visited is not null)
                        {
                            FileEntry target;
                            try
                            {
                                target = IsNameSurrogateFolder(child) ? _fs.GetEntryFollowingLinks(full) : child;
                            }
                            catch (FileSystemException ex)
                            {
                                onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), Describe(ex.Kind, "link")));
                                continue;
                            }

                            if (!visited.Add((target.VolumeSerial, target.FileId)))
                            {
                                onSkip(new WalkSkip(PathDisplay.ToDisplayPath(full), "Skipped: this folder was already scanned via another path (a link leads to it)."));
                                continue;
                            }
                        }
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

        return isDirectory && _excludeFolders is not null && _excludeFolders.Match(slashed).HasMatches;
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
