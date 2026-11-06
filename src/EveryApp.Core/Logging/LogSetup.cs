using EveryApp.Core.Settings;
using Serilog;
using Serilog.Events;

namespace EveryApp.Core.Logging;

/// <summary>Creates the log for one app: one file per day, 14 days kept, secrets scrubbed.</summary>
public static class LogSetup
{
    private const int FilesKept = 14;

    /// <summary>Creates the logger for <paramref name="appName"/>, writing under <see cref="AppPaths.Logs"/>.</summary>
    public static ILogger Create(string appName) => Create(appName, AppPaths.Logs);

    internal static ILogger Create(string appName, string directory)
    {
        ValidateAppName(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Information)
            .WriteTo.File(
                new ScrubbingFormatter(),
                Path.Combine(directory, appName + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: FilesKept,
                shared: false)
            .CreateLogger();
    }

    /// <summary>Flushes and closes a logger made by <see cref="Create(string)"/>. Call it when the app exits.</summary>
    public static void Close(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        (logger as IDisposable)?.Dispose();
    }

    private static void ValidateAppName(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        if (appName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || appName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The app name can only contain letters, digits and simple punctuation.", nameof(appName));
        }
    }
}
