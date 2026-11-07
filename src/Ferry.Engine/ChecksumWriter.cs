using EveryApp.Core.FileSystem;
using EveryApp.Core.Hashing;
using EveryApp.Core.Jobs;

namespace Ferry.Engine;

/// <summary>Collects "hash  relative-path" lines and writes &lt;destination&gt;\ferry-checksums.&lt;algorithm&gt; through a part file, never over an existing file.</summary>
internal sealed class ChecksumWriter(IFileSystem fs, string destination, HashAlgorithmKind algorithm, JobJournalWriter journal, JobOutcome outcome)
{
    private readonly List<string> _lines = [];

    public void Add(string hashHex, string targetKey) => _lines.Add($"{hashHex}  {targetKey}");

    public void Write(CancellationToken ct)
    {
        if (_lines.Count == 0)
        {
            return;
        }

        var name = "ferry-checksums." + algorithm.ToString().ToLowerInvariant();
        var target = Path.Combine(destination, name);
        if (TreePaths.TryGet(fs, target, follow: false) is not null)
        {
            target = NameAllocator.NextFreeName(fs, target) ?? target;
        }

        var part = FileCopier.NewPartPath(target);
        journal.Append(new JournalRecord(JournalRecordKind.FilePartStarted, Path.GetFileName(target), 0, DateTime.UtcNow, null, Path.GetFileName(part)));
        var data = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', _lines) + "\n");
        var created = false;
        try
        {
            var writer = fs.CreateWrite(part, 0, unbuffered: false);
            created = true;
            try
            {
                writer.WriteAsync(data, 0, ct).AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            fs.Move(part, target, overwrite: false);
            created = false;
        }
        catch (FileSystemException ex)
        {
            outcome.Problems.Add(new Problem(TreePaths.Display(target), ex.Kind, CopyMessages.ChecksumsNotWritten));
        }
        finally
        {
            if (created)
            {
                TreePaths.TryDelete(fs, part);
            }
        }
    }
}
