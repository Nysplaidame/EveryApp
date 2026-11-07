using System.Text.Json;
using System.Text.Json.Serialization;
using EveryApp.Core.Hashing;

namespace Ferry.Engine;

/// <summary>Everything needed to run (or resume) a job. Stored as the journal header, so it is untrusted when read back.</summary>
internal sealed record JobSpec(int Version, IReadOnlyList<string> Sources, string Destination, CopyMode Mode, CopyOptions Options)
{
    public const int CurrentVersion = 1;

    public string ToJson() => JsonSerializer.Serialize(this, FerryJsonContext.Default.JobSpec);

    /// <summary>Parses and checks a stored job. Throws <see cref="InvalidDataException"/> for anything that does not describe a sane job.</summary>
    public static JobSpec Parse(string json)
    {
        JobSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize(json, FerryJsonContext.Default.JobSpec);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The saved job is damaged, so it cannot be resumed.", ex);
        }

        if (spec is null || !spec.IsValid())
        {
            throw new InvalidDataException("The saved job is damaged, so it cannot be resumed.");
        }

        return spec;
    }

    public bool IsValid()
    {
        var o = Options;
        return Version == CurrentVersion
            && Sources is { Count: > 0 }
            && Sources.All(s => !string.IsNullOrEmpty(s) && Path.IsPathFullyQualified(s))
            && !string.IsNullOrEmpty(Destination) && Path.IsPathFullyQualified(Destination)
            && Enum.IsDefined(Mode)
            && o is not null
            && o.Exclude is not null && o.Exclude.All(e => e is not null)
            && o.BufferSizeBytes is >= 0 and <= FileCopier.MaxBufferSizeBytes
            && o.SpeedLimitBytesPerSecond is null or > 0
            && Enum.IsDefined(o.IfExists)
            && Enum.IsDefined(o.IfUnreadable)
            && Enum.IsDefined(o.HashAlgorithm);
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobSpec))]
[JsonSerializable(typeof(HashAlgorithmKind))]
internal sealed partial class FerryJsonContext : JsonSerializerContext;
