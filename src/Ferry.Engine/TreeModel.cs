using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

internal enum TreeItemKind
{
    Folder,
    File,
    Link,
}

/// <summary>One thing to do. <see cref="Key"/> is the path relative to the destination folder (also the journal key).</summary>
internal sealed record TreeItem(TreeItemKind Kind, FileEntry Entry, string Key);

/// <summary>One source item (file or folder) given by the caller, with its work in order.</summary>
internal sealed record TreeRoot(string Path, FileEntry Entry, string Key, bool SameVolume, bool MoveByRename, List<TreeItem> Items);

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

    public static string Display(string path) => PathDisplay.ToDisplayPath(path);
}
