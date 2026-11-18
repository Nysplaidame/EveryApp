using System.Globalization;

namespace EveryApp.Core.Preflight;

/// <summary>
/// All preflight wording in one place so the tone can be reviewed together. Plain language: say what is wrong and why it matters.
/// </summary>
internal static class PreflightMessages
{
    public static string PathTooLong(int length, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"This path is too long for Windows ({length} characters; the limit is {limit}), so it may fail to copy.");

    public static string ReservedName(string name) =>
        $"\"{name}\" is a name Windows keeps for itself, so it can't be created on the destination.";

    public static string TrailingDotOrSpace() =>
        "A name ending in a dot or a space is not handled properly by many programs and drives, so it may be lost or renamed.";

    public static string IllegalChar() =>
        "This name contains a character the network drive does not allow, so it can't be copied as it is.";

    public static string CaseCollision() =>
        "Another item in the same folder has the same name apart from capital letters, so one would replace the other.";

    public static string TooLargeForFat32() =>
        "This file is 4 GB or bigger, which is more than a drive formatted as FAT32 can hold.";

    public static string NotEnoughSpace(string drive, long needed, long free) =>
        $"Drive {drive} needs {FormatSize(needed)} but only {FormatSize(free)} is free.";

    public static string ZoneMarkerNotKept() =>
        "This drive can't keep the 'downloaded from the internet' marker, so Windows may not warn you about these files.";

    public static string FreeSpaceUnknown(string drive) =>
        $"Couldn't check how much free space is on {drive}, so the copy might run out of room.";

    /// <summary>Binary units shown with the usual names (1 GB = 1024 MB), as Windows does.</summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
        }

        var number = value >= 10 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
        return number + " " + units[unit];
    }
}
