namespace Ferry.Bench;

/// <summary>
/// Test data from the M1 plan, Task 16: large = 20 x 1 GiB; small = 10,000 files of 1-64 KiB; mixed = half of each by bytes.
/// Contents are random (fixed seed) so nothing can compress or de-duplicate them.
/// </summary>
internal static class DataSets
{
    private const long GiB = 1024L * 1024 * 1024;

    public static void Generate(string kind, string folder, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new IOException($"The folder is not empty: {folder}");
        }

        Directory.CreateDirectory(folder);
        var random = new Random(16);
        switch (kind)
        {
            case "large":
                Large(folder, 20 / Math.Min(scale, 20), random);
                break;
            case "small":
                Small(folder, 10_000 / scale, random);
                break;
            case "mixed":
                // 2 GiB of large files and 2 GiB of small ones (about 64,000 files of 1-64 KiB, 32.5 KiB on average).
                Large(folder, Math.Max(1, 2 / scale), random);
                SmallBytes(Path.Combine(folder, "small"), 2 * GiB / scale, random);
                break;
            default:
                throw new ArgumentException($"Choose large, small or mixed, not '{kind}'.");
        }
    }

    private static void Large(string folder, int count, Random random)
    {
        var buffer = new byte[4 * 1024 * 1024];
        for (var i = 0; i < count; i++)
        {
            using var file = new FileStream(Path.Combine(folder, $"large-{i:D2}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 0);
            for (long done = 0; done < GiB; done += buffer.Length)
            {
                random.NextBytes(buffer);
                file.Write(buffer);
            }
        }
    }

    private static void Small(string folder, int count, Random random)
    {
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < count; i++)
        {
            WriteSmall(folder, i, buffer, random);
        }
    }

    private static void SmallBytes(string folder, long bytes, Random random)
    {
        Directory.CreateDirectory(folder);
        var buffer = new byte[64 * 1024];
        long done = 0;
        for (var i = 0; done < bytes; i++)
        {
            done += WriteSmall(folder, i, buffer, random);
        }
    }

    /// <summary>100 files per sub-folder, like a real tree.</summary>
    private static int WriteSmall(string folder, int index, byte[] buffer, Random random)
    {
        var size = random.Next(1024, (64 * 1024) + 1);
        var span = buffer.AsSpan(0, size);
        random.NextBytes(span);
        var dir = Path.Combine(folder, $"d{index / 100:D4}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"f{index:D6}.dat"), span.ToArray());
        return size;
    }
}
