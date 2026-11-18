using EveryApp.Core.FileSystem;

namespace Ferry.Engine;

/// <summary>Finds a free "name (2).ext" style name next to a taken one.</summary>
internal static class NameAllocator
{
    private const int MaxNumber = 9999;

    /// <summary>"name (2).ext", "name (3).ext"... the first one not taken. Null if none is free.</summary>
    public static string? NextFreeName(IFileSystem fs, string target)
    {
        var folder = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileName(target);
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var n = 2; n <= MaxNumber; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!IsTaken(fs, candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the name is taken, or cannot be checked (then it is treated as taken: never risk using someone's name).</summary>
    private static bool IsTaken(IFileSystem fs, string path)
    {
        try
        {
            fs.GetEntry(path);
            return true;
        }
        catch (FileSystemException ex)
        {
            return ex.Kind != IoErrorKind.NotFound;
        }
    }
}
