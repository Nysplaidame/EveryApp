using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>
/// Remembers each volume's sector size for the length of one job. A sector size belongs to the volume, but asking the file system opens a
/// handle on the path, which is slow on the step that opens every file. Volumes are told apart by their volume id (never by drive letter, so
/// mount points and one volume under two letters cannot be mixed up). A path whose volume cannot be identified is asked about every time.
/// </summary>
internal sealed class SectorSizeCache(IFileSystem fs)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sector size of the volume holding <paramref name="path"/> (which need not exist yet). Same answers and errors as
    /// <see cref="IFileSystem.GetSectorSize"/>; a failed look-up is never remembered.
    /// </summary>
    public int Get(string path)
    {
        var volume = fs.GetVolumeId(path);
        if (volume is null)
        {
            return fs.GetSectorSize(path);
        }

        lock (_lock)
        {
            if (_byVolume.TryGetValue(volume, out var known))
            {
                return known;
            }

            var size = fs.GetSectorSize(path);
            _byVolume[volume] = size;
            return size;
        }
    }
}
