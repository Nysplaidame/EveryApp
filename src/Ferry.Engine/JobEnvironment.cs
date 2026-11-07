using EveryApp.Core.Power;
using Microsoft.Win32;

namespace Ferry.Engine;

/// <summary>Keeps the PC awake while a job runs. Seam for tests.</summary>
public interface ISleepBlocker
{
    /// <summary>Blocks sleep until the returned handle is disposed.</summary>
    IDisposable Acquire();
}

/// <summary>Blocks system sleep (not the screen turning off) with SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED).</summary>
public sealed class WindowsSleepBlocker : ISleepBlocker
{
    public static WindowsSleepBlocker Instance { get; } = new();

    public IDisposable Acquire() => KeepAwake.Acquire();
}

/// <summary>Facts about the destination that pre-flight checks need. Seam for tests.</summary>
public interface IDestinationInfo
{
    /// <summary>The destination's file system as Windows names it ("NTFS", "ReFS", "FAT32", "exFAT"), "SMB" for a network share, "" if unknown.</summary>
    string FileSystemOf(string path);

    /// <summary>Whether Windows' long-path support is switched on (other programs can then open paths over 260 characters).</summary>
    bool LongPathsEnabled { get; }
}

/// <summary>Reads the file system with DriveInfo (network drives and UNC paths count as "SMB") and the long-path switch from the registry.</summary>
public sealed class WindowsDestinationInfo : IDestinationInfo
{
    private readonly Lazy<bool> _longPaths = new(ReadLongPathsEnabled);

    public static WindowsDestinationInfo Instance { get; } = new();

    public bool LongPathsEnabled => _longPaths.Value;

    public string FileSystemOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var plain = path.Replace('/', '\\');
        if (plain.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || (plain.StartsWith(@"\\", StringComparison.Ordinal) && !plain.StartsWith(@"\\?\", StringComparison.Ordinal)))
        {
            return "SMB";
        }

        if (plain.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            plain = plain[4..];
        }

        if (plain.Length < 2 || plain[1] != ':')
        {
            return string.Empty;
        }

        try
        {
            var drive = new DriveInfo(plain[..1]);
            return drive.DriveType == DriveType.Network ? "SMB" : drive.DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool ReadLongPathsEnabled()
    {
        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 0) is int value && value == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
