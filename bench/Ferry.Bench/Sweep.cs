using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using Ferry.Engine;

namespace Ferry.Bench;

/// <summary>
/// Times FileCopier (verify on, as the app ships) over every file in a folder, once per tuning choice. One lever changes at a time from the
/// current defaults (buffer size, pipeline depth, unbuffered threshold, order of files in a folder), so each row is compared with the first.
/// Choices run in turn within each round so slow drift in the drives affects all of them alike. Reports the median of the rounds.
/// </summary>
internal static class Sweep
{
    private const int MiB = 1024 * 1024;

    // The values the M1 plan (Task 16, step 3) asks to try.
    private static readonly int[] BufferMiB = [1, 4, 8, 16];
    private static readonly int[] Depths = [2, 4, 8];
    private static readonly int[] ThresholdMiB = [1, 8, 32];

    private sealed record Choice(string Lever, string Value, int BufferBytes, int Depth, long ThresholdBytes, bool SmallestFirst);

    private sealed record Row(string Lever, string Value, double MedianSeconds, double MiBPerSecond, double VsDefault);

    private static List<Choice> Choices()
    {
        var d = CopierTuning.Default;
        var buffer = FileCopier.DefaultBufferSizeBytes;
        var list = new List<Choice> { new("default", $"{buffer / MiB} MiB, depth {d.PipelineDepth}, {d.UnbufferedThresholdBytes / MiB} MiB, smallest first", buffer, d.PipelineDepth, d.UnbufferedThresholdBytes, true) };
        foreach (var b in BufferMiB.Where(b => b * MiB != buffer))
        {
            list.Add(new("buffer", $"{b} MiB", b * MiB, d.PipelineDepth, d.UnbufferedThresholdBytes, true));
        }

        foreach (var depth in Depths.Where(x => x != d.PipelineDepth))
        {
            list.Add(new("pipeline depth", depth.ToString(CultureInfo.InvariantCulture), buffer, depth, d.UnbufferedThresholdBytes, true));
        }

        foreach (var t in ThresholdMiB.Where(t => t * (long)MiB != d.UnbufferedThresholdBytes))
        {
            list.Add(new("unbuffered threshold", $"{t} MiB", buffer, d.PipelineDepth, t * (long)MiB, true));
        }

        list.Add(new("file order", "by name", buffer, d.PipelineDepth, d.UnbufferedThresholdBytes, false));
        return list;
    }

    public static async Task<int> RunAsync(string source, string dest, int rounds, string? jsonPath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        source = Path.GetFullPath(source);
        dest = Path.GetFullPath(dest);
        var (s, d) = (source.TrimEnd('\\') + '\\', dest.TrimEnd('\\') + '\\');
        if (s.StartsWith(d, StringComparison.OrdinalIgnoreCase) || d.StartsWith(s, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The destination must not be the source, inside it, or around it.");
        }

        if (!Directory.Exists(source))
        {
            throw new IOException($"The source folder does not exist: {source}");
        }

        var files = new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var bytes = files.Sum(f => f.Length);
        var choices = Choices();
        var times = choices.ToDictionary(c => c, _ => new List<double>());
        var fs = new WindowsFileSystem();
        using var pool = new BufferPool();
        var flushed = true;
        Console.WriteLine($"Sweep: {files.Count} files, {bytes / (double)MiB:F0} MiB, {choices.Count} choices x {rounds} rounds.");

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var choice in choices)
            {
                Clear(dest);
                flushed &= StandbyCache.TryFlush(out _);
                var copier = new FileCopier(fs, pool, TimeProvider.System, new CopierTuning(choice.Depth, choice.ThresholdBytes));
                var options = new CopyOptions { BufferSizeBytes = choice.BufferBytes };
                var watch = Stopwatch.StartNew();
                await CopyAllAsync(copier, options, source, dest, files, choice.SmallestFirst).ConfigureAwait(false);
                watch.Stop();
                times[choice].Add(watch.Elapsed.TotalSeconds);
                Console.WriteLine($"  round {round}: {choice.Lever} = {choice.Value}: {watch.Elapsed.TotalSeconds:F2} s");
            }
        }

        Clear(dest);
        var baseline = Median(times[choices[0]]);
        var rows = choices.Select(c =>
        {
            var m = Median(times[c]);
            return new Row(c.Lever, c.Value, m, bytes / (double)MiB / m, m / baseline);
        }).ToList();

        Console.WriteLine();
        Console.WriteLine("| Lever | Value | Median s | MiB/s | Time vs default |");
        Console.WriteLine("|---|---|---:|---:|---:|");
        foreach (var r in rows)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Lever} | {r.Value} | {r.MedianSeconds:F2} | {r.MiBPerSecond:F0} | {r.VsDefault:F3} |"));
        }

        if (!flushed)
        {
            Console.WriteLine("Standby cache not cleared between runs (needs an administrator).");
        }

        if (jsonPath is not null)
        {
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new { files = files.Count, bytes, rounds, cacheCleared = flushed, rows })).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task CopyAllAsync(FileCopier copier, CopyOptions options, string source, string dest, List<FileInfo> files, bool smallestFirst)
    {
        var pause = new PauseGate();
        var progress = new Progress<FileProgress>();
        foreach (var folder in files.GroupBy(f => f.DirectoryName!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, folder.Key));
            Directory.CreateDirectory(target);
            var ordered = smallestFirst
                ? folder.OrderBy(f => f.Length).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : folder.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var file in ordered)
            {
                var outcome = await copier.CopyAsync(file.FullName, Path.Combine(target, file.Name), options, pause, progress, CancellationToken.None).ConfigureAwait(false);
                if (!outcome.Success)
                {
                    throw new IOException($"Copy failed: {file.FullName}: {outcome.Problem?.Message}");
                }
            }
        }
    }

    private const string Marker = ".ferry-bench-out";

    /// <summary>
    /// Empties <paramref name="dest"/> only if it is new, empty or marked by these tools (same rule as bench.ps1). Never a drive root;
    /// links inside are removed as links (Directory.Delete does not follow them).
    /// </summary>
    private static void Clear(string dest)
    {
        if (Path.GetPathRoot(dest) is { } root && string.Equals(dest.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Refusing to empty a drive root: {dest}");
        }

        var marker = Path.Combine(dest, Marker);
        if (Directory.Exists(dest) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            throw new IOException($"Refusing to empty {dest}: it is not empty and was not made by the bench tools (no {Marker} file).");
        }

        Directory.CreateDirectory(dest);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "Ferry benchmark output: the bench tools empty this folder before every run.");
        }

        foreach (var entry in new DirectoryInfo(dest).EnumerateFileSystemInfos())
        {
            if (entry.Name == Marker)
            {
                continue;
            }

            if (entry is DirectoryInfo folder)
            {
                folder.Delete(recursive: !folder.Attributes.HasFlag(FileAttributes.ReparsePoint));
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
