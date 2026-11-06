using EveryApp.Core.FileSystem;

namespace EveryApp.Core.Paths;

/// <summary>Containment checks that cannot be fooled by dot segments, slash style, case, the long-path prefix or look-alike folder names.</summary>
public static class PathSafety
{
    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies below it. Both paths are first turned into their
    /// canonical <c>\\?\</c> form (so <c>..</c> is resolved and the plain and prefixed spellings of one path compare equal), then compared
    /// case-insensitively on whole path segments (<c>C:\a</c> does not contain <c>C:\ab</c>).
    /// Input is accepted only as a full path: a drive path (<c>C:\x</c> or <c>C:/x</c>) or a UNC path (<c>\\server\share\x</c>), in either the plain
    /// or the <c>\\?\</c> form. Anything else returns false (never throws): empty text, embedded nulls, device paths, relative paths,
    /// drive-relative paths (<c>C:x</c>) and rooted paths without a drive (<c>\x</c>), which would otherwise be resolved against the current
    /// directory. <c>\\?\</c> input is never resolved, so it also returns false when it holds a <c>.</c> or <c>..</c> segment, an empty segment or
    /// a <c>/</c>. This is a text check: it does not look at links on disk.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        if (root is null || candidate is null)
        {
            return false;
        }

        if (!TryCanonical(root, out var r) || !TryCanonical(candidate, out var c))
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

    private static bool TryCanonical(string path, out string canonical)
    {
        canonical = string.Empty;
        if (path.Length == 0 || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                if (!IsCleanLongForm(path))
                {
                    return false;
                }
            }
            else
            {
                var slashed = path.Replace('/', '\\');
                var isDrive = slashed.Length >= 3 && char.IsAsciiLetter(slashed[0]) && slashed[1] == ':' && slashed[2] == '\\';
                var isUnc = slashed.Length > 2 && slashed[0] == '\\' && slashed[1] == '\\' && slashed[2] is not ('\\' or '?' or '.');
                if (!isDrive && !isUnc)
                {
                    return false;
                }
            }

            canonical = Normalize(path);
            return true;
        }
        catch (FileSystemException)
        {
            return false;
        }
    }

    // \\?\ input is trusted to mean exactly what it says, so it must already be in its final form.
    private static bool IsCleanLongForm(string path)
    {
        if (path.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path.AsSpan(4);
        int minSegments;
        if (rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\')
        {
            rest = rest[3..];
            minSegments = 0;
        }
        else if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[4..];
            minSegments = 2; // server and share
        }
        else
        {
            return false;
        }

        var segments = 0;
        while (!rest.IsEmpty)
        {
            var i = rest.IndexOf('\\');
            var segment = i < 0 ? rest : rest[..i];
            rest = i < 0 ? default : rest[(i + 1)..];
            if (segment.IsEmpty)
            {
                if (rest.IsEmpty)
                {
                    break; // a single trailing separator
                }

                return false;
            }

            if (segment is "." or "..")
            {
                return false;
            }

            segments++;
        }

        return segments >= minSegments;
    }
}
