namespace EveryApp.Core.FileSystem;

/// <summary>Helpers for showing paths to people. Never use the result to access files.</summary>
public static class PathDisplay
{
    /// <summary>
    /// Strips the <c>\\?\</c> (or <c>\\?\UNC\</c>) prefix for user-facing text, but only when the plain path still names the same item
    /// (names ending in a dot or space, dot segments and similar keep the prefix).
    /// </summary>
    public static string ToDisplayPath(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var plain = WindowsFileSystem.StripPrefix(fullPath);
        if (ReferenceEquals(plain, fullPath))
        {
            return fullPath;
        }

        try
        {
            return string.Equals(Path.GetFullPath(plain), plain, StringComparison.Ordinal) ? plain : fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return fullPath;
        }
    }
}
