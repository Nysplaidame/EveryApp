using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

internal enum TreeItemKind
{
    Folder,
    File,
    Link,
}

/// <summary>
/// One thing to do. <see cref="Key"/> is the path relative to the destination folder. <see cref="JournalKey"/> is the same path prefixed with
/// the source's index (<c>0\photos\a.jpg</c>), so two sources with the same name never share journal records.
/// </summary>
internal sealed record TreeItem(TreeItemKind Kind, FileEntry Entry, string Key, string JournalKey);

/// <summary>One source item (file or folder) given by the caller, with its work in order.</summary>
internal sealed record TreeRoot(string Path, FileEntry Entry, string Key, string JournalKey, bool SameVolume, bool MoveByRename, List<TreeItem> Items);

/// <summary>Small path and file-system helpers shared by the tree-copy components. Lookups never throw.</summary>
internal static class TreePaths
{
    private const uint NameSurrogateBit = 0x20000000;

    public static FileEntry? TryGet(IFileSystem fs, string path, bool follow)
    {
        try
        {
            return follow ? fs.GetEntryFollowingLinks(path) : fs.GetEntry(path);
        }
        catch (FileSystemException)
        {
            return null;
        }
    }

    public static FileEntry? NearestExisting(IFileSystem fs, string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (TryGet(fs, p, follow: true) is { } e)
            {
                return e;
            }
        }

        return null;
    }

    public static void TryDelete(IFileSystem fs, string path)
    {
        try
        {
            fs.Delete(path);
        }
        catch (FileSystemException)
        {
            // Already gone, or not empty: either way nothing to do.
        }
    }

    public static bool SameObject(FileEntry a, FileEntry b) => a.VolumeSerial == b.VolumeSerial && a.FileId == b.FileId;

    public static bool IsNameSurrogate(FileEntry entry) => entry.IsReparsePoint && (entry.ReparseTag & NameSurrogateBit) != 0;

    /// <summary>The last path segment, or "" for a drive or share root (whose contents go straight into the destination).</summary>
    public static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0 || trimmed.EndsWith(':') || (trimmed.StartsWith(@"\\?", StringComparison.Ordinal) && trimmed.Length <= 4))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(trimmed);
        return name.EndsWith(':') ? string.Empty : name;
    }

    public static string Combine(string folderKey, string relative) => folderKey.Length == 0 ? relative : folderKey + "\\" + relative;

    /// <summary>The journal key of a destination-relative key: the source index first (a drive root's own key is just the index).</summary>
    public static string JournalKey(int sourceIndex, string key)
    {
        var index = sourceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key.Length == 0 ? index : index + "\\" + key;
    }

    public static string Display(string path) => PathDisplay.ToDisplayPath(path);

    /// <summary>
    /// Joins a path read from the journal (untrusted) to <paramref name="root"/> and checks the result is still inside the root, after full
    /// resolution of <c>..</c> and rooted parts. False means the record is corrupt and must not be used.
    /// </summary>
    public static bool TryContain(string root, string relative, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var (prefix, plainRoot) = root.StartsWith(@"\\?\", StringComparison.Ordinal) ? (@"\\?\", root[4..]) : (string.Empty, root);
            var r = Path.GetFullPath(plainRoot).TrimEnd('\\');
            var candidate = Path.GetFullPath(Path.Combine(r, relative));
            if (!candidate.Equals(r, StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            full = prefix + candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
