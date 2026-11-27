using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using EveryApp.Core.Jobs;

namespace EveryApp.Core.Tests.Jobs;

/// <summary>Builds well-formed, correctly checksummed journal lines the way a tamperer who knows the format would.</summary>
internal static class JournalTestHelper
{
    public static byte[] ForgeLine(JournalRecordKind kind, string? relativePath, string? detail = null, string? targetPath = null) =>
        ForgeRecord(new JournalRecord(kind, relativePath, 1L, DateTime.UnixEpoch, null, detail, targetPath));

    public static byte[] ForgeRecord(JournalRecord record)
    {
        var r = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["Kind"] = (int)record.Kind,
            ["RelativePath"] = record.RelativePath,
            ["Length"] = record.Length,
            ["SourceLastWriteUtc"] = record.SourceLastWriteUtc,
            ["Hash"] = record.Hash,
            ["Detail"] = record.Detail,
            ["TargetPath"] = record.TargetPath,
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
