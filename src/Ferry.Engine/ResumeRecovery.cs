using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>
/// Resume after a crash: deletes exactly the part files the journal names (never by pattern), and recognises a file that was renamed into
/// place just before the crash (part record, no done record, destination holding the source's exact data) so it is finished, not recopied.
/// </summary>
internal sealed class ResumeRecovery
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

    /// <summary>Removes the journaled part files of files that are not done yet.</summary>
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
                TreePaths.TryDelete(_fs, Path.Combine(_destination, part.PartRelativePath));
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

        foreach (var targetKey in parts.Select(p => p.TargetRelativePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(_destination, targetKey);
            if (TreePaths.TryGet(_fs, target, follow: false) is not { } existing || existing.Attributes.HasFlag(FileAttributes.Directory) || existing.Length != item.Entry.Length)
            {
                continue;
            }

            try
            {
                var source = await Verifier.HashAsync(_fs, item.Entry.FullPath, unbuffered: false, algorithm, ct).ConfigureAwait(false);
                if (await Verifier.MatchesAsync(_fs, target, source, algorithm, ct).ConfigureAwait(false))
                {
                    return (targetKey, source);
                }
            }
            catch (FileSystemException)
            {
                // Could not check it: copy normally (the conflict rule then decides).
            }
        }

        return null;
    }
}

