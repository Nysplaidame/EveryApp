using System.Collections.Concurrent;
using System.Globalization;

namespace EveryApp.Core.Scheduling;

/// <summary>Asks Windows whether each physical disk has a seek penalty (spinning) or not (solid state). Answers are remembered per disk.</summary>
public sealed class WindowsDriveKindProbe : IDriveKindProbe
{
    private const string DiskPrefix = "disk:";

    private readonly IVolumeApi _api;
    private readonly ConcurrentDictionary<uint, bool> _seekPenalty = new();

    /// <summary>The shared probe that talks to the real system.</summary>
    public static WindowsDriveKindProbe Instance { get; } = new(new NativeVolumeApi());

    internal WindowsDriveKindProbe(IVolumeApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <inheritdoc />
    public DriveKind KindOf(DeviceSet devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Ids.Count == 0)
        {
            return DriveKind.Unknown;
        }

        var spinning = false;
        var unknown = false;
        foreach (var id in devices.Ids)
        {
            if (!id.StartsWith(DiskPrefix, StringComparison.Ordinal)
                || !uint.TryParse(id.AsSpan(DiskPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                unknown = true;
                continue;
            }

            if (!_seekPenalty.TryGetValue(number, out var penalty))
            {
                var answer = _api.HasSeekPenalty(number);
                if (answer is null)
                {
                    unknown = true;
                    continue;
                }

                penalty = answer.Value;
                _seekPenalty[number] = penalty;
            }

            spinning |= penalty;
        }

        if (spinning)
        {
            return DriveKind.Spinning;
        }

        return unknown ? DriveKind.Unknown : DriveKind.SolidState;
    }
}
