using System.Text.RegularExpressions;

namespace EveryApp.Core.Logging;

/// <summary>Removes secrets and the user's profile path from text before it is logged.</summary>
public static partial class SecretScrubber
{
    private const int TimeoutMilliseconds = 250;
    private const string Redacted = "***";
    private const string ProfilePlaceholder = "%USERPROFILE%";

    // Used when scrubbing cannot finish in time: better to lose the line than to leak it.
    private const string Withheld = "[log text withheld: could not be checked for secrets]";

    private static readonly string? ProfilePath = NormalizeProfile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Returns the text with secrets replaced by <c>***</c> and the profile path replaced by <c>%USERPROFILE%</c>.</summary>
    public static string Scrub(string message) => Scrub(message, ProfilePath);

    internal static string Scrub(string message, string? profilePath)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        try
        {
            var text = ReplaceProfile(message, NormalizeProfile(profilePath));
            text = PrivateKeyBlock().Replace(text, Redacted);
            text = AuthorizationHeader().Replace(text, "${prefix}" + Redacted);
            text = KeyValueSecret().Replace(text, "${prefix}" + Redacted);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    private static string? NormalizeProfile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('\\', '/');

        // Never treat a drive root such as "C:" as a profile; that would mangle every path.
        return trimmed.Length < 4 ? null : trimmed;
    }

    private static string ReplaceProfile(string text, string? profile)
    {
        if (profile is null)
        {
            return text;
        }

        // Longest forms first so the extended-length prefix does not survive.
        text = ReplaceBounded(text, @"\\?\" + profile);
        text = ReplaceBounded(text, profile);
        return ReplaceBounded(text, profile.Replace('\\', '/'));
    }

    // Case-insensitive replace that only matches a whole folder name (so "Ali" never matches inside "Alicia").
    private static string ReplaceBounded(string text, string find)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        while (true)
        {
            var index = text.IndexOf(find, position, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var end = index + find.Length;
            var bounded = end == text.Length || !char.IsLetterOrDigit(text[end]);
            builder.Append(text, position, index - position);
            builder.Append(bounded ? ProfilePlaceholder : text.Substring(index, find.Length));
            position = end;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    [GeneratedRegex(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\z)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        @"(?<prefix>Authorization\s*:\s*(?:Bearer|Basic)\s+)[^\s""',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        @"(?<prefix>(?:token|password|pwd|secret|api_?key)\s*=\s*)(?:""[^""]*""|'[^']*'|[^\s&;,""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex KeyValueSecret();
}
