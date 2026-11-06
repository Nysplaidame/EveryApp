using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace EveryApp.Core.Logging;

/// <summary>
/// The only formatter the log file uses. It builds the whole line (time, level, message, exception)
/// and scrubs it before anything is written, so no log text can skip the scrubber.
/// </summary>
internal sealed class ScrubbingFormatter(Func<string, string>? scrub = null) : ITextFormatter
{
    private readonly Func<string, string> _scrub = scrub ?? SecretScrubber.Scrub;

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var line = new StringWriter(CultureInfo.InvariantCulture);
        line.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        line.Write(" [");
        line.Write(LevelCode(logEvent.Level));
        line.Write("] ");
        logEvent.RenderMessage(line, CultureInfo.InvariantCulture);
        if (logEvent.Exception is not null)
        {
            line.WriteLine();
            line.Write(logEvent.Exception.ToString());
        }

        output.WriteLine(_scrub(line.ToString()));
    }

    private static string LevelCode(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };
}
