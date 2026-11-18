using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveryApp.Core.Logging;
using Ferry.Engine;

namespace Ferry.Cli;

internal sealed class CliOutput(bool json) : IProgress<JobProgress>
{
    private readonly Lock _gate = new();
    private long _lastProgress;
    private bool _updating;

    public Guid? JobId { get; set; }

    public static string Safe(string text) =>
        new(SecretScrubber.Scrub(text).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    public void Report(JobProgress value)
    {
        lock (_gate)
        {
            // Keep progress cheap, including when stdout is a pipe. Always emit the first snapshot.
            var now = Environment.TickCount64;
            if (_lastProgress != 0 && now - _lastProgress < 100 && value.FilesDone != value.FilesTotal)
            {
                return;
            }

            _lastProgress = now;
            if (json)
            {
                Write(new CliEvent("progress", JobId, Phase: value.Phase, BytesDone: value.BytesDone,
                    BytesTotal: value.BytesTotal, FilesDone: value.FilesDone, FilesTotal: value.FilesTotal,
                    BytesPerSecond: value.BytesPerSecond, SecondsRemaining: value.Remaining?.TotalSeconds));
            }
            else if (!Console.IsOutputRedirected)
            {
                var percent = value.BytesTotal > 0 ? 100d * value.BytesDone / value.BytesTotal : 0;
                var remaining = value.Remaining is { } time ? $"about {Math.Ceiling(time.TotalMinutes):0} min left" : "estimating time";
                Console.Write(string.Create(CultureInfo.InvariantCulture,
                    $"\r{percent:0}% · {value.BytesDone / 1e9:0.0} of {value.BytesTotal / 1e9:0.0} GB · {value.BytesPerSecond / 1e6:0} MB/s · {remaining}          "));
                _updating = true;
            }
        }
    }

    public void Problem(string message, string? path = null) =>
        Emit(new CliEvent("problem", JobId, Message: Safe(message), Path: path is null ? null : Safe(path)),
            path is null ? message : $"{message} {path}");

    public void Plan(PlannedItem item) =>
        Emit(new CliEvent("progress", JobId, Phase: "Preview", Message: item.Action.ToString(),
            Source: Safe(item.Source), Destination: Safe(item.Destination)),
            $"{item.Action}: {item.Source} → {item.Destination}");

    public void Preview(ResumeRequest request) =>
        Emit(new CliEvent("progress", JobId, Phase: "Preview", Message: request.Mode.ToString(),
            Source: Safe(string.Join(", ", request.Sources)), Destination: Safe(request.Destination)),
            $"{request.Mode}: {string.Join(", ", request.Sources)} → {request.Destination}");

    public void History(Guid id, DateTime date, string sources, string destination, string result) =>
        Emit(new CliEvent("progress", id, Phase: "History", Message: Safe(result),
            Source: Safe(sources), Destination: Safe(destination), Date: date),
            $"{id:D}  {date:u}  {sources} → {destination}  {result}");

    public int Result(int code, string message, CopyResult? copy = null)
    {
        Emit(new CliEvent("result", JobId, Message: Safe(message), ExitCode: code,
            Copied: copy?.Copied, Skipped: copy?.Skipped, Failed: copy?.Failed, AllVerified: copy?.AllVerified), message);
        return code;
    }

    private void Emit(CliEvent value, string text)
    {
        lock (_gate)
        {
            if (json)
            {
                Write(value);
            }
            else
            {
                if (_updating)
                {
                    Console.WriteLine();
                    _updating = false;
                }

                Console.WriteLine(Safe(text));
            }
        }
    }

    private static void Write(CliEvent value) => Console.WriteLine(JsonSerializer.Serialize(value, CliJsonContext.Default.CliEvent));
}

internal sealed record CliEvent(string Type, Guid? JobId = null, string? Message = null, string? Path = null,
    int? ExitCode = null, string? Phase = null, long? BytesDone = null, long? BytesTotal = null,
    int? FilesDone = null, int? FilesTotal = null, double? BytesPerSecond = null, double? SecondsRemaining = null,
    int? Copied = null, int? Skipped = null, int? Failed = null, bool? AllVerified = null,
    string? Source = null, string? Destination = null, DateTime? Date = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliEvent))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
