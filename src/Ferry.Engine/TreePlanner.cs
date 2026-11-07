using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;
using EveryApp.Core.Preflight;

namespace Ferry.Engine;

/// <summary>
/// The "Checking" phase: refuses overlapping source and destination, lists every source in work order, runs the pre-flight checks and
/// builds the dry-run plan. Reads only; never writes.
/// </summary>
internal sealed class TreePlanner(IFileSystem fs, JobSpec spec, IReadOnlySet<string> completed, JobOutcome outcome)
{
    private readonly CopyOptions _options = spec.Options;
    private readonly bool _isMove = spec.Mode == CopyMode.Move;
    private readonly string _destination = spec.Destination;

    /// <summary>Warnings let the copy go ahead: Ferry can still write the item, other programs may struggle with it.</summary>
    public static bool IsBlocking(PreflightIssue issue) =>
        issue.Code is not (PreflightCode.PathTooLong or PreflightCode.TrailingDotOrSpace or PreflightCode.FreeSpaceUnknown);

    /// <summary>Refuses a destination inside a folder being copied, or an item that is already where it would go. Judged by on-disk identity.</summary>
    public void CheckOverlap()
    {
        foreach (var source in spec.Sources)
        {
            FileEntry identity;
            try
            {
                identity = fs.GetEntryFollowingLinks(source);
            }
            catch (FileSystemException)
            {
                continue; // reported when the source is listed
            }

            if (identity.Attributes.HasFlag(FileAttributes.Directory)
                && (PathSafety.IsWithin(source, _destination) || AnyAncestorIs(_destination, identity)))
            {
                outcome.Issues.Add(new PreflightIssue(PreflightCode.DestinationInsideSource, TreePaths.Display(source), CopyMessages.DestinationInsideSource));
                continue;
            }

            var name = TreePaths.NameOf(source);
            if (name.Length > 0 && TreePaths.TryGet(fs, Path.Combine(_destination, name), follow: true) is { } target && TreePaths.SameObject(target, identity))
            {
                outcome.Issues.Add(new PreflightIssue(PreflightCode.DestinationInsideSource, TreePaths.Display(source), CopyMessages.SameAsSource));
            }
        }
    }

    private bool AnyAncestorIs(string path, FileEntry identity)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (TreePaths.TryGet(fs, p, follow: true) is { } e && TreePaths.SameObject(e, identity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists every source and puts the work in order: each folder before its contents, small files first within a folder.</summary>
    public List<TreeRoot> Plan(FileEntry? destinationEntry)
    {
        var roots = new List<TreeRoot>();
        for (var index = 0; index < spec.Sources.Count; index++)
        {
            var source = spec.Sources[index];
            FileEntry entry;
            try
            {
                entry = fs.GetEntry(source);
            }
            catch (FileSystemException ex)
            {
                outcome.Failed++;
                outcome.Problems.Add(new Problem(TreePaths.Display(source), ex.Kind, CopyMessages.For(ex.Kind, whileWriting: false, CopyMessages.DriveOf(_destination))));
                continue;
            }

            var key = TreePaths.NameOf(source);
            var isFolder = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (!isFolder && key.Length == 0)
            {
                continue;
            }

            var rootJournalKey = TreePaths.JournalKey(index, key);
            if (key.Length > 0 && completed.Contains(rootJournalKey))
            {
                continue; // moved as a whole before a crash
            }

            var sameVolume = destinationEntry is not null && entry.VolumeSerial == destinationEntry.VolumeSerial;
            if (_isMove && isFolder && !sameVolume && TreePaths.IsNameSurrogate(entry))
            {
                outcome.Problems.Add(new Problem(TreePaths.Display(source), IoErrorKind.None, CopyMessages.LinkNotMovedAcrossDrives));
                continue;
            }

            var items = new List<TreeItem>();
            if (isFolder)
            {
                if (key.Length > 0)
                {
                    items.Add(new TreeItem(TreeItemKind.Folder, entry, key, rootJournalKey));
                }

                items.AddRange(Walk(source, key, index));
            }
            else
            {
                items.Add(new TreeItem(TreeItemKind.File, entry, key, rootJournalKey));
            }

            var byRename = _isMove && sameVolume && isFolder && key.Length > 0 && _options.Exclude.Count == 0
                && TreePaths.TryGet(fs, Path.Combine(_destination, key), follow: false) is null;
            roots.Add(new TreeRoot(entry.FullPath, entry, key, rootJournalKey, _isMove && sameVolume, byRename, items));
        }

        return roots;
    }

    private List<TreeItem> Walk(string root, string rootKey, int index)
    {
        // A move never follows links, whatever the option says.
        var walker = new TreeWalker(fs, followReparsePoints: !_isMove && _options.FollowReparsePoints, _options.Exclude);
        var folders = new List<TreeItem>();
        var files = new Dictionary<string, List<TreeItem>>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, List<TreeItem>>(StringComparer.OrdinalIgnoreCase);

        static void AddTo(Dictionary<string, List<TreeItem>> map, TreeItem item)
        {
            var parent = Path.GetDirectoryName(item.Key) ?? string.Empty;
            if (!map.TryGetValue(parent, out var list))
            {
                map[parent] = list = [];
            }

            list.Add(item);
        }

        TreeItem Item(TreeItemKind kind, WalkItem w)
        {
            var key = TreePaths.Combine(rootKey, w.RelativePath);
            return new TreeItem(kind, w.Entry, key, TreePaths.JournalKey(index, key));
        }

        Action<WalkItem>? onLink = _isMove ? link => AddTo(links, Item(TreeItemKind.Link, link)) : null;
        foreach (var w in walker.Walk(root, OnSkip, onLink))
        {
            if (w.Entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                folders.Add(Item(TreeItemKind.Folder, w));
            }
            else
            {
                AddTo(files, Item(TreeItemKind.File, w));
            }
        }

        var ordered = new List<TreeItem>();
        void AddContents(string folderKey)
        {
            if (files.TryGetValue(folderKey, out var list))
            {
                ordered.AddRange(list.OrderBy(i => i.Entry.Length).ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase));
            }

            if (links.TryGetValue(folderKey, out var linkList))
            {
                ordered.AddRange(linkList);
            }
        }

        AddContents(rootKey);
        foreach (var folder in folders)
        {
            ordered.Add(folder);
            AddContents(folder.Key);
        }

        return ordered;
    }

    private void OnSkip(WalkSkip skip) =>
        outcome.Problems.Add(new Problem(skip.Path, skip.Reason == CopyMessages.LinkSkipped ? IoErrorKind.None : IoErrorKind.Other, skip.Reason));

    /// <summary>Totals for progress, then the pre-flight checks on everything that will really be written (same-drive moves are renames).</summary>
    public void RunPreflight(List<TreeRoot> roots, ProgressTracker progress, IDestinationInfo destinationInfo)
    {
        var items = new List<WalkItem>();
        foreach (var root in roots.Where(r => !r.SameVolume))
        {
            foreach (var item in root.Items)
            {
                if (item.Kind != TreeItemKind.Link && !completed.Contains(item.JournalKey))
                {
                    items.Add(new WalkItem(item.Entry, item.Key));
                }
            }
        }

        foreach (var root in roots)
        {
            foreach (var item in root.Items.Where(i => i.Kind == TreeItemKind.File && !completed.Contains(i.JournalKey)))
            {
                progress.AddToTotal(1, item.Entry.Length);
            }
        }

        if (items.Count > 0)
        {
            var fileSystem = destinationInfo.FileSystemOf(_destination);
            outcome.Issues.AddRange(new PreflightRunner(fs).Check(items, _destination, fileSystem, destinationInfo.LongPathsEnabled));
        }

        progress.Report();
    }

    /// <summary>What would happen, without writing anything (no journal, no scheduler, no folders).</summary>
    public CopyResult DryRun(List<TreeRoot> roots)
    {
        var plan = new List<PlannedItem>();
        var copied = 0;
        var skipped = 0;
        foreach (var root in roots)
        {
            foreach (var item in root.Items)
            {
                var target = Path.Combine(_destination, item.Key);
                var source = TreePaths.Display(item.Entry.FullPath);
                if (item.Kind != TreeItemKind.File)
                {
                    if (TreePaths.TryGet(fs, target, follow: false) is null)
                    {
                        plan.Add(new PlannedItem(source, TreePaths.Display(target), item.Kind == TreeItemKind.Folder ? PlannedAction.CreateFolder : PlannedAction.Move));
                    }

                    continue;
                }

                var (action, to) = PlanFile(item.Entry, target);
                plan.Add(new PlannedItem(source, TreePaths.Display(to), action));
                if (action == PlannedAction.Skip)
                {
                    skipped++;
                }
                else if (action != PlannedAction.Ask)
                {
                    copied++;
                }
            }
        }

        return new CopyResult(copied, skipped, outcome.Failed, [.. outcome.Problems], [.. outcome.Issues], AllVerified: false) { Plan = plan };
    }

    private (PlannedAction Action, string Target) PlanFile(FileEntry source, string target)
    {
        var existing = TreePaths.TryGet(fs, target, follow: false);
        if (existing is null)
        {
            return (_isMove ? PlannedAction.Move : PlannedAction.Copy, target);
        }

        return _options.IfExists switch
        {
            ConflictRule.Ask => (PlannedAction.Ask, target),
            ConflictRule.Overwrite => (PlannedAction.Replace, target),
            ConflictRule.OverwriteIfNewer => source.LastWriteTimeUtc > existing.LastWriteTimeUtc ? (PlannedAction.Replace, target) : (PlannedAction.Skip, target),
            ConflictRule.Rename or ConflictRule.KeepBoth => (PlannedAction.CopyWithNewName, NameAllocator.NextFreeName(fs, target) ?? target),
            _ => (PlannedAction.Skip, target),
        };
    }
}
