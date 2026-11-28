using System.Globalization;
using System.Text;
using EveryApp.Core.FileSystem;
using EveryApp.Core.Paths;

namespace Ferry.Engine;

/// <summary>Saves rescue details without replacing any existing report or following a redirected folder.</summary>
internal sealed class LostReport(IFileSystem fs, TimeProvider time)
{
    public async Task<Problem> WriteAsync(string source, string destination, IReadOnlyList<LostRange> lost, string? root, CancellationToken ct)
    {
        const string Suffix = ".ferry-lost.txt";
        var name = Path.GetFileName(destination);
        if (name.Length + Suffix.Length > FileCopier.MaxNameLength)
        {
            var keep = FileCopier.MaxNameLength - Suffix.Length - 9;
            if (char.IsHighSurrogate(name[keep - 1]))
            {
                keep--;
            }

            name = name[..keep] + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        }

        var target = Path.Combine(Path.GetDirectoryName(destination)!, name + Suffix);
        var text = new StringBuilder();
        text.AppendLine(PathDisplay.ToDisplayPath(source));
        text.AppendLine(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        foreach (var range in lost)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Bytes {range.Offset:N0} to {range.Offset + range.Length - 1:N0} ({Size(range.Length)}) couldn't be read and were filled with blanks."));
        }

        var summary = CopyMessages.Rescued(Size(lost.Sum(r => (long)r.Length)));
        var part = FileCopier.NewPartPath(target);
        var ours = false;
        try
        {
            var data = Encoding.UTF8.GetBytes(text.ToString());
            var handle = fs.CreateWrite(part, data.Length, unbuffered: false);
            ours = true;
            await using (handle.ConfigureAwait(false))
            {
                if (root is not null && !PathSafety.IsSameOrUnderFinal(root, handle.GetFinalPath()))
                {
                    // This is our newly created file, so it is safe to delete through its own handle.
                    handle.DeleteOnClose();
                    ours = false;
                    return new Problem(source, IoErrorKind.BadBlock, summary + CopyMessages.LostReportNotWritten);
                }

                await handle.WriteAsync(data, 0, ct).ConfigureAwait(false);
                await handle.FlushAsync().ConfigureAwait(false);
            }

            fs.Move(part, target, overwrite: false);
            ours = false;
            return new Problem(source, IoErrorKind.BadBlock, summary + " — see " + Path.GetFileName(target));
        }
        catch (FileSystemException)
        {
            return new Problem(source, IoErrorKind.BadBlock, summary + CopyMessages.LostReportNotWritten);
        }
        finally
        {
            if (ours)
            {
                try
                {
                    fs.Delete(part);
                }
                catch (FileSystemException)
                {
                    // Best effort: never touch an existing report.
                }
            }
        }
    }

    private static string Size(long bytes) => bytes % 1024 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024:N0} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes");
}
