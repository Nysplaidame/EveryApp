using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

/// <summary>Builds well-formed, correctly checksummed journal lines the way a tamperer who knows the format would.</summary>
internal static class JournalTestHelper
{
    public static byte[] ForgeLine(JournalRecordKind kind, string? relativePath, string? detail = null, string? targetPath = null)
    {
        var r = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["Kind"] = (int)kind,
            ["RelativePath"] = relativePath,
            ["Length"] = 1L,
            ["SourceLastWriteUtc"] = DateTime.UnixEpoch,
            ["Hash"] = null,
            ["Detail"] = detail,
            ["TargetPath"] = targetPath,
        });
        var crc = Crc32.HashToUInt32(r);
        var prefix = Encoding.UTF8.GetBytes("{\"crc\":" + crc + ",\"r\":");
        var line = new byte[prefix.Length + r.Length + 2];
        prefix.CopyTo(line, 0);
        r.CopyTo(line, prefix.Length);
        line[^2] = (byte)'}';
        line[^1] = (byte)'\n';
        return line;
    }
}
