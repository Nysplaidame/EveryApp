using System.Globalization;
using BenchmarkDotNet.Running;

namespace Ferry.Bench;

/// <summary>
/// Benchmark tool for Ferry. Commands:
///   micro [BenchmarkDotNet args]                 micro-benchmarks (buffer pool, hashing, copy pipeline)
///   generate large|small|mixed &lt;folder&gt; [--scale N]   creates a test data set (N divides the default size, for quick trials)
///   sweep &lt;source&gt; &lt;dest&gt; [--rounds N] [--json &lt;file&gt;]  times FileCopier with each tuning choice
///   flush                                         empties the Windows standby cache (needs an administrator)
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: Ferry.Bench micro | generate <large|small|mixed> <folder> [--scale N] | sweep <source> <dest> [--rounds N] [--json <file>] | flush");
            return 2;
        }

        try
        {
            switch (args[0])
            {
                case "micro":
                    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args[1..]);
                    return 0;
                case "generate" when args.Length >= 3:
                    DataSets.Generate(args[1], args[2], IntOption(args, "--scale", 1));
                    return 0;
                case "sweep" when args.Length >= 3:
                    return await Sweep.RunAsync(args[1], args[2], IntOption(args, "--rounds", 3), StringOption(args, "--json")).ConfigureAwait(false);
                case "flush":
                    return StandbyCache.TryFlush(out var why) ? 0 : Fail(why);
                default:
                    return Fail($"Unknown command or missing arguments: {string.Join(' ', args)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int IntOption(string[] args, string name, int fallback) =>
        StringOption(args, name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    private static string? StringOption(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
