using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Paths;
using Ferry.Engine;

namespace Ferry.Cli;

internal static class FolderVerifier
{
    public static async Task<int> RunAsync(string left, string right, CliOutput output, CancellationToken ct)
    {
        var fs = new WindowsFileSystem();
        var problems = 0;
        void Problem(string message, string path)
        {
            problems++;
            output.Problem(message, path);
        }

        Dictionary<string, FileEntry> Read(string root)
        {
            var entries = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in new TreeWalker(fs, false, []).Walk(root, skip => Problem(skip.Reason, skip.Path)))
            {
                ct.ThrowIfCancellationRequested();
                if (item.Entry.IsReparsePoint && (item.Entry.ReparseTag & 0x20000000) != 0)
                {
                    Problem("Skipped a link to another file.", item.RelativePath);
                }
                else if (!entries.TryAdd(item.RelativePath, item.Entry))
                {
                    Problem("Two names differ only in letter case.", item.RelativePath);
                }
            }

            return entries;
        }

        var a = Read(left);
        var b = Read(right);
        foreach (var (relative, entry) in a)
        {
            ct.ThrowIfCancellationRequested();
            if (!b.Remove(relative, out var other))
            {
                Problem("Missing from the second folder.", relative);
                continue;
            }

            var directory = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (directory != other.Attributes.HasFlag(FileAttributes.Directory))
            {
                Problem("A file and a folder have the same name.", relative);
                continue;
            }

            if (directory)
            {
                continue;
            }

            try
            {
                // Different sizes already differ: skip reading both files.
                if (entry.Length != other.Length)
                {
                    Problem("File contents differ.", relative);
                    continue;
                }

                var hash = await Verifier.HashAsync(fs, entry.FullPath, true, HashAlgorithmKind.XxHash3, ct).ConfigureAwait(false);
                if (!await Verifier.MatchesAsync(fs, other.FullPath, hash, HashAlgorithmKind.XxHash3, ct).ConfigureAwait(false))
                {
                    Problem("File contents differ.", relative);
                }
            }
            catch (FileSystemException)
            {
                Problem("The file could not be read.", relative);
            }
        }

        foreach (var relative in b.Keys)
        {
            ct.ThrowIfCancellationRequested();
            Problem("Only in the second folder.", relative);
        }

        return output.Result(problems == 0 ? ExitCodes.Success : ExitCodes.Problems,
            problems == 0 ? "The folders match." : $"Found {problems} differences or unreadable items.");
    }
}
