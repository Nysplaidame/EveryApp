using System.Text.RegularExpressions;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>
/// Resume after a crash: deletes exactly the part files the journal names (never by pattern), and recognises a file that was renamed into
/// place just before the crash (part record, no done record, destination holding the source's exact data) so it is finished, not recopied.
/// The journal is untrusted: every path it names must stay inside the destination after full resolution, and a part file must have the
/// name shape Ferry gives part files and sit next to its target; anything else is ignored as corruption.
/// </summary>
internal sealed partial class ResumeRecovery
{
    private readonly IFileSystem _fs;
    private readonly string _destination;
    private readonly IReadOnlySet<string> _completed;
    private readonly Dictionary<string, List<JournalPart>> _partsByKey = new(StringComparer.OrdinalIgnoreCase);

    public ResumeRecovery(IFileSystem fs, string destination, JobJournal? journal)
    {
        _fs = fs;
        _destination = destination;
        _completed = journal?.CompletedPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in journal?.PartFiles ?? [])
        {
            if (!_partsByKey.TryGetValue(part.RelativePath, out var list))
            {
                _partsByKey[part.RelativePath] = list = [];
            }

            list.Add(part);
        }
    }

    /// <summary>Keys journaled as done (copied, or moved as a whole folder).</summary>
    public IReadOnlySet<string> Completed => _completed;

    /// <summary>The name shape <see cref="FileCopier.NewPartPath"/> produces: something, a dot, 8 lowercase hex digits, ".ferrypart".</summary>
    public static bool LooksLikeOurPart(string name) => PartName().IsMatch(name);

    /// <summary>Removes the journaled part files of files that are not done yet (only those that pass every check).</summary>
    public void DeleteJournaledParts()
    {
        foreach (var (key, parts) in _partsByKey)
        {
            if (_completed.Contains(key))
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (IsTrustworthy(part, out var partPath, out _))
                {
                    TreePaths.TryDelete(_fs, partPath);
                }
            }
        }
    }

    /// <summary>
    /// When this file has a part record and the target recorded in it (never a name guessed from the part file, which may be shortened)
    /// already holds exactly the source's data, returns that target's key and the hash; otherwise null (copy normally).
    /// </summary>
    public async Task<(string TargetKey, byte[] Hash)?> FindCommittedAsync(TreeItem item, HashAlgorithmKind algorithm, CancellationToken ct)
    {
        if (!_partsByKey.TryGetValue(item.JournalKey, out var parts))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (!IsTrustworthy(part, out _, out var target) || !seen.Add(target))
            {
                continue;
            }

            if (TreePaths.TryGet(_fs, target, follow: false) is not { } existing
                || existing.Attributes.HasFlag(FileAttributes.Directory) || existing.IsReparsePoint || existing.Length != item.Entry.Length)
            {
                continue;
            }

            try
            {
                var source = await Verifier.HashAsync(_fs, item.Entry.FullPath, unbuffered: false, algorithm, ct).ConfigureAwait(false);
                if (await Verifier.MatchesAsync(_fs, target, source, algorithm, ct).ConfigureAwait(false))
                {
                    return (part.TargetRelativePath, source);
                }
            }
            catch (FileSystemException)
            {
                // Could not check it: copy normally (the conflict rule then decides).
            }
        }

        return null;
    }

    private bool IsTrustworthy(JournalPart part, out string partPath, out string targetPath)
    {
        targetPath = string.Empty;
        return TreePaths.TryContain(_destination, part.PartRelativePath, out partPath)
            && TreePaths.TryContain(_destination, part.TargetRelativePath, out targetPath)
            && LooksLikeOurPart(Path.GetFileName(part.PartRelativePath))
            && string.Equals(Path.GetDirectoryName(partPath), Path.GetDirectoryName(targetPath), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^.+\.[0-9a-f]{8}\.ferrypart$", RegexOptions.CultureInvariant)]
    private static partial Regex PartName();
}
