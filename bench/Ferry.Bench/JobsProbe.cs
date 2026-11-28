using System.Diagnostics;
using System.Globalization;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Jobs;
using EveryApp.Core.Scheduling;
using Ferry.Engine;

namespace Ferry.Bench;

/// <summary>
/// Times whole copy jobs (the real TreeCopier, file system, drive lookup and drive-kind probe) once per worker count, each into its own
/// fresh marked folder under the destination, which is emptied again after the timing so repeated runs do not fill the drive.
/// </summary>
internal static class JobsProbe
{
    private const int MiB = 1024 * 1024;

    public static async Task<int> RunAsync(string source, string destRoot, int[] workerCounts, bool verify)
    {
        if (workerCounts.Length == 0 || workerCounts.Any(w => w < 1))
        {
            throw new ArgumentException("Worker counts must be 1 or more, for example --workers 1,2,4,8.");
        }

        if (WindowsElevationCheck.Instance.IsElevated)
        {
            Console.Error.WriteLine("The copy engine refuses to run as an administrator, so this timing cannot run. Start a normal (not elevated) terminal and try again.");
            return 1;
        }

        source = Path.GetFullPath(source);
        destRoot = Path.GetFullPath(destRoot);
        var (s, d) = (source.TrimEnd('\\') + '\\', destRoot.TrimEnd('\\') + '\\');
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
        var resolver = new WindowsDeviceResolver();
        var kind = WindowsDriveKindProbe.Instance.KindOf(resolver.Resolve(source).Union(resolver.Resolve(destRoot)));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Jobs: {files.Count} files, {bytes / (double)MiB:F0} MiB, verify {(verify ? "on" : "off")}."));
        Console.WriteLine($"Drive kind reported for source and destination: {kind}" + (kind == DriveKind.SolidState ? "." : " (more than one worker is not allowed here, so every count runs one file at a time)."));

        // Every count copies into a fresh destination, so only the source can be cached. Read it once, write nothing, so the source is warm for
        // all counts and the first count is not penalised for running first.
        Console.WriteLine("Warming the read cache...");
        var scratch = new byte[MiB];
        foreach (var file in files)
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            while (stream.Read(scratch, 0, scratch.Length) > 0)
            {
            }
        }

        var jobsDir = Path.Combine(Path.GetTempPath(), "ferry-bench-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobsDir);
        try
        {
            foreach (var workers in workerCounts)
            {
                var run = Path.Combine(destRoot, "jobs-run-" + Guid.NewGuid().ToString("N"));
                Sweep.Clear(run);
                try
                {
                    var copier = new TreeCopier(
                        new WindowsFileSystem(), new DriveAwareScheduler(resolver), new SkipResolver(), jobsDir, null, null, null,
                        WindowsElevationCheck.Instance, WindowsDriveKindProbe.Instance, workers);
                    var watch = Stopwatch.StartNew();
                    var result = await copier.RunAsync(
                        Guid.NewGuid(), [source], run, CopyMode.Copy, new CopyOptions { Verify = verify }, new PauseGate(), new Progress<JobProgress>(), CancellationToken.None)
                        .ConfigureAwait(false);
                    watch.Stop();
                    if (result.Failed > 0 || result.Stopped || result.PreflightIssues.Count > 0)
                    {
                        throw new IOException($"The copy did not finish cleanly with {workers} workers: {result.Failed} failed. {(result.Problems.Count > 0 ? result.Problems[0].Message : string.Empty)}");
                    }

                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  workers {workers}: {watch.Elapsed.TotalSeconds:F2} s ({bytes / (double)MiB / watch.Elapsed.TotalSeconds:F0} MiB/s), {result.Copied} copied"));
                }
                finally
                {
                    Sweep.Clear(run);
                    Directory.Delete(run, recursive: true);
                }
            }
        }
        finally
        {
            Directory.Delete(jobsDir, recursive: true);
        }

        return 0;
    }

    /// <summary>The destination is always a fresh folder, so nothing should clash; if it does, leave the file alone.</summary>
    private sealed class SkipResolver : IConflictResolver
    {
        public ValueTask<ConflictRule> ResolveAsync(FileEntry source, FileEntry existing, CancellationToken ct) => ValueTask.FromResult(ConflictRule.Skip);
    }
}
