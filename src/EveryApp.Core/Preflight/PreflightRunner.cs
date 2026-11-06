using System.Buffers;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;

namespace EveryApp.Core.Preflight;

/// <summary>
/// Looks for problems before anything is copied. One pass over the items, no per-item regex; messages are plain language.
/// Locked files are not checked here: the copier finds them when it opens them.
/// </summary>
public sealed class PreflightRunner
{
    private const int MaxPathLength = 259;
    private const long Fat32Limit = 4_294_967_296;
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";

    // Characters an SMB share rejects: : * ? " < > | and the control characters 0x00-0x1F.
    private static readonly SearchValues<char> SmbIllegal = SearchValues.Create(
        ":*?\"<>|" + string.Concat(Enumerable.Range(0, 32).Select(c => (char)c)));

    private readonly IFileSystem _fs;

    public PreflightRunner(IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        _fs = fs;
    }

    public IReadOnlyList<PreflightIssue> Check(IReadOnlyList<WalkItem> items, string destinationRoot, string destinationFileSystem, bool longPathsEnabled)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(destinationRoot);
        ArgumentNullException.ThrowIfNull(destinationFileSystem);

        var issues = new List<PreflightIssue>();
        var isSmb = destinationFileSystem.Equals("SMB", StringComparison.OrdinalIgnoreCase);
        var isFat32 = destinationFileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
        var rootLength = PlainRoot(destinationRoot).Length + 1; // plus the separator before the relative path
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var item in items)
        {
            var rel = item.RelativePath;
            var isDirectory = (item.Entry.Attributes & FileAttributes.Directory) != 0;
            string? display = null;

            void Add(PreflightCode code, string message) =>
                issues.Add(new PreflightIssue(code, display ??= PathDisplay.ToDisplayPath(item.Entry.FullPath), message));

            if (!isDirectory)
            {
                total += item.Entry.Length;
            }

            if (!longPathsEnabled)
            {
                var length = rootLength + rel.Length + (isDirectory ? 1 : 0);
                if (length > MaxPathLength)
                {
                    Add(PreflightCode.PathTooLong, PreflightMessages.PathTooLong(length, MaxPathLength + 1));
                }
            }

            ScanSegments(rel, out var reserved, out var trailing);
            if (reserved is not null)
            {
                Add(PreflightCode.ReservedName, PreflightMessages.ReservedName(reserved));
            }

            if (trailing)
            {
                Add(PreflightCode.TrailingDotOrSpace, PreflightMessages.TrailingDotOrSpace());
            }

            if (isSmb && rel.AsSpan().IndexOfAny(SmbIllegal) >= 0)
            {
                Add(PreflightCode.IllegalCharForDestination, PreflightMessages.IllegalChar());
            }

            var split = rel.LastIndexOf('\\');
            var parent = split < 0 ? string.Empty : rel[..split];
            var name = split < 0 ? rel : rel[(split + 1)..];
            if (!names.TryGetValue(parent, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                names[parent] = set;
            }

            if (!set.Add(name))
            {
                Add(PreflightCode.CaseCollision, PreflightMessages.CaseCollision());
            }

            if (isFat32 && !isDirectory && item.Entry.Length >= Fat32Limit)
            {
                Add(PreflightCode.TooLargeForFat32, PreflightMessages.TooLargeForFat32());
            }
        }

        CheckSpace(issues, destinationRoot, total);
        return issues;
    }

    private void CheckSpace(List<PreflightIssue> issues, string destinationRoot, long total)
    {
        long free;
        try
        {
            free = _fs.GetFreeSpace(destinationRoot);
        }
        catch (FileSystemException)
        {
            return; // free space unknown: the copy itself still fails safely if the drive fills up
        }

        if (total > free - free / 100)
        {
            issues.Add(new PreflightIssue(PreflightCode.NotEnoughSpace, PathDisplay.ToDisplayPath(destinationRoot), PreflightMessages.NotEnoughSpace(total, free)));
        }
    }

    private static string PlainRoot(string root)
    {
        var plain = root;
        if (plain.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            plain = @"\\" + plain[LongUncPrefix.Length..];
        }
        else if (plain.StartsWith(LongPrefix, StringComparison.Ordinal))
        {
            plain = plain[LongPrefix.Length..];
        }

        return plain.TrimEnd('\\');
    }

    /// <summary>Walks the path segments once. <paramref name="reserved"/> is the first reserved segment (or null).</summary>
    private static void ScanSegments(string rel, out string? reserved, out bool trailing)
    {
        reserved = null;
        trailing = false;
        var span = rel.AsSpan();
        while (!span.IsEmpty)
        {
            var cut = span.IndexOf('\\');
            var segment = cut < 0 ? span : span[..cut];
            span = cut < 0 ? default : span[(cut + 1)..];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (segment[^1] is '.' or ' ')
            {
                trailing = true;
            }

            if (reserved is null && IsReserved(segment))
            {
                reserved = segment.ToString();
            }
        }
    }

    /// <summary>Reserved device name before the first dot, ignoring trailing spaces and case.</summary>
    private static bool IsReserved(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        if (stem.Length == 3)
        {
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        if (stem.Length == 4)
        {
            var digit = stem[3];
            var isDigit = digit is (>= '0' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';
            return isDigit
                && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}
