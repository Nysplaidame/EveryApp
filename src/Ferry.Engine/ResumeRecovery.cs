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
    private readonly Action<Problem> _skipped;
    private readonly HashSet<string> _completed;
    private readonly IReadOnlySet<string> _notOnDisk;
    private readonly DateTime? _jobStartedUtc;
    private readonly DateTime? _lastActivityUtc;
    private readonly Dictionary<string, List<JournalPart>> _partsByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="skipped">Told about each leftover that was left alone because it is not really inside the destination.</param>
    public ResumeRecovery(IFileSystem fs, string destination, JobJournal? journal, Action<Problem> skipped)
    {
        _fs = fs;
        _skipped = skipped;
        _destination = destination;

        // A file renamed into place without being forced to disk is not trusted as done: after a power cut it may hold blanks. It goes through
        // FindCommittedAsync like a file renamed just before a crash (kept when it holds the source's exact data), else DamagedTarget.
        _notOnDisk = journal?.NotOnDiskPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _completed = new HashSet<string>(journal?.CompletedPaths ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        _completed.ExceptWith(_notOnDisk);
        _jobStartedUtc = journal?.StartedUtc;
        _lastActivityUtc = journal?.LastActivityUtc;
        IsResume = journal?.LastActivityUtc is not null;
        foreach (var part in journal?.PartFiles ?? [])
        {
            if (!_partsByKey.TryGetValue(part.RelativePath, out var list))
            {
                _partsByKey[part.RelativePath] = list = [];
            }

            list.Add(part);
        }
    }

    /// <summary>Keys journaled as done (copied, or moved as a whole folder) and trusted as they are.</summary>
    public IReadOnlySet<string> Completed => _completed;

    /// <summary>Journaled as done, but renamed into place without being forced to disk: check the file again before trusting it.</summary>
    public bool IsNotOnDisk(string key) => _notOnDisk.Contains(key);

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
                    DeletePart(partPath);
                }
            }
        }
    }

    /// <summary>
    /// Deletes through a handle opened without following links, and only if the file's final path is inside the destination: a link in
    /// the destination (or in the path the journal names) cannot send this delete somewhere else.
    /// </summary>
    private void DeletePart(string partPath)
    {
        try
        {
            if (!_fs.DeleteFileContained(partPath, _destination))
            {
                LeftAlone(partPath);
            }
        }
        catch (FileSystemException)
        {
            // Already gone, or in use: either way nothing to do.
        }
    }

    private void LeftAlone(string path) =>
        _skipped(new Problem(TreePaths.Display(path), IoErrorKind.None, CopyMessages.ResumeLeftAlone));

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

            if (!IsInsideDestination(target))
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

    /// <summary>True when this is a resume (the journal already held records), not a fresh job.</summary>
    public bool IsResume { get; }

    /// <summary>
    /// Resume of a copy that may have skipped forcing small files to disk: checks the file this job would have written for <paramref name="item"/>
    /// (the target of its latest part record, else its normal name). A power cut can keep the rename yet lose the data and even the journal's
    /// last records, so this does not rely on a done record. The file counts as this job's own only if it is a plain file in the same folder as
    /// the normal name, inside the destination, the source's size, and dated like the source (its dates were kept) or within the job's run. A
    /// file changed after the job stopped is not this job's own and is left to the conflict rule.
    /// </summary>
    public async Task<Recheck> RecheckAsync(TreeItem item, HashAlgorithmKind algorithm, CancellationToken ct)
    {
        var key = item.Key;
        if (_partsByKey.TryGetValue(item.JournalKey, out var parts) && IsTrustworthy(parts[^1], out _, out _)
            && string.Equals(Path.GetDirectoryName(parts[^1].TargetRelativePath), Path.GetDirectoryName(item.Key), StringComparison.OrdinalIgnoreCase))
        {
            key = parts[^1].TargetRelativePath; // e.g. "name (2).ext" under the rename rule
        }

        if (!TreePaths.TryContain(_destination, key, out var target) || TreePaths.TryGet(_fs, target, follow: false) is not { } existing
            || existing.Attributes.HasFlag(FileAttributes.Directory) || existing.IsReparsePoint || existing.Length != item.Entry.Length
            || !(existing.LastWriteTimeUtc == item.Entry.LastWriteTimeUtc || WithinJob(existing.LastWriteTimeUtc)) || !IsInsideDestination(target))
        {
            return new Recheck(RecheckResult.NotOurs, key);
        }

        try
        {
            var source = await Verifier.HashAsync(_fs, item.Entry.FullPath, unbuffered: false, algorithm, ct).ConfigureAwait(false);
            return new Recheck(await Verifier.MatchesAsync(_fs, target, source, algorithm, ct).ConfigureAwait(false) ? RecheckResult.Intact : RecheckResult.Damaged, key);
        }
        catch (FileSystemException)
        {
            return new Recheck(RecheckResult.NotOurs, key); // could not check it: copy normally (the conflict rule then decides)
        }
    }

    /// <summary>Written while the job ran: from its start to a minute after the journal was last written (that time can be a little early).</summary>
    private bool WithinJob(DateTime when) =>
        _jobStartedUtc is { } start && _lastActivityUtc is { } last && when >= start && when <= last.AddMinutes(1);

    /// <summary>The recovered target itself, opened without following links, must really sit inside the destination before it is touched.</summary>
    private bool IsInsideDestination(string target)
    {
        try
        {
            if (_fs.IsContained(target, _destination))
            {
                return true;
            }

            LeftAlone(target);
        }
        catch (FileSystemException)
        {
            // Not there (or unreadable): nothing to recover.
        }

        return false;
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

internal enum RecheckResult
{
    /// <summary>Not a file this job wrote (or it could not be checked): handle it like any existing file.</summary>
    NotOurs,

    /// <summary>This job's file, holding exactly the source's data.</summary>
    Intact,

    /// <summary>This job's file, but its data does not match (a power cut): replace it.</summary>
    Damaged,
}

/// <summary>Result of <see cref="ResumeRecovery.RecheckAsync"/>; <paramref name="TargetKey"/> is the file it looked at, relative to the destination.</summary>
internal readonly record struct Recheck(RecheckResult Result, string TargetKey);
