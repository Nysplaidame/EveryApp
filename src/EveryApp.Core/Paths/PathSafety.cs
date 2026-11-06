using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Paths;

/// <summary>Containment checks that cannot be fooled by dot segments, slash style, case, the long-path prefix or look-alike folder names.</summary>
public static class PathSafety
{
    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies below it. Both paths are first turned into their
    /// canonical <c>\\?\</c> form (so <c>..</c> is resolved and the plain and prefixed spellings of one path compare equal), then compared
    /// case-insensitively on whole path segments (<c>C:\a</c> does not contain <c>C:\ab</c>). Anything that cannot be normalised
    /// (empty, device paths, embedded nulls) is not within. This is a text check: it does not look at links on disk.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        if (root is null || candidate is null)
        {
            return false;
        }

        string r;
        string c;
        try
        {
            r = Normalize(root);
            c = Normalize(candidate);
        }
        catch (FileSystemException)
        {
            return false;
        }

        // Keep the trailing separator only on a bare drive or share root, where it is part of the name.
        var rootEndsWithSeparator = r.EndsWith('\\');
        if (rootEndsWithSeparator && !IsBareRoot(r))
        {
            r = r.TrimEnd('\\');
            rootEndsWithSeparator = false;
        }

        c = c.Length > r.Length && c.EndsWith('\\') && !IsBareRoot(c) ? c.TrimEnd('\\') : c;

        if (!c.StartsWith(r, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return c.Length == r.Length || rootEndsWithSeparator || c[r.Length] == '\\';
    }

    /// <summary>
    /// The canonical long form (<c>\\?\C:\...</c> or <c>\\?\UNC\server\share\...</c>) of a path, with <c>..</c> resolved and forward slashes converted.
    /// Throws <see cref="FileSystemException"/> (InvalidName) for empty, null-containing or device paths.
    /// </summary>
    public static string Normalize(string path) => WindowsFileSystem.Normalize(path);

    private static bool IsBareRoot(string longForm) =>
        longForm.Length == 7 && longForm[5] == ':' // \\?\C:\
        || longForm.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) && longForm.AsSpan(8).Count('\\') == 2 && longForm.EndsWith('\\');
}
