using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>
/// Remembers a sector size for the length of one job, once per job root. Asking the file system opens a handle on the path, which is slow on
/// the step that opens every file. A root's own path is asked about (exactly what a per-file ask would resolve to, links included), and every
/// file the job treats as inside that root reuses the answer. A failed ask is never remembered: the caller's own path is asked instead.
/// </summary>
internal sealed class SectorSizeCache(IFileSystem fs)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sector size for <paramref name="path"/>, taken from <paramref name="root"/> (a job root it lies in). Without a root, or when the root
    /// cannot be asked, <paramref name="path"/> itself is asked, with the same errors as <see cref="IFileSystem.GetSectorSize"/>.
    /// </summary>
    public int Get(string? root, string path)
    {
        if (root is null)
        {
            return fs.GetSectorSize(path);
        }

        lock (_lock)
        {
            if (_byRoot.TryGetValue(root, out var known))
            {
                return known;
            }

            try
            {
                var size = fs.GetSectorSize(root);
                _byRoot[root] = size;
                return size;
            }
            catch (FileSystemException)
            {
            }
        }

        return fs.GetSectorSize(path);
    }
}
