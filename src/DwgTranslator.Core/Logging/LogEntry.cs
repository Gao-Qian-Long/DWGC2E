namespace DwgTranslator.Core.Logging;

/// <summary>
/// Structured log entry for display in the UI log viewer and file output.
/// Immutable once created.
/// </summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Exception { get; init; }
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Optional correlation ID to group related log entries (e.g. a full translation operation).
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional elapsed time in milliseconds for timed operations.
    /// Null when not a timed operation.
    /// </summary>
    public double? DurationMs { get; init; }

    /// <summary>
    /// Indicates the origin of this log entry: "App", "CAD", "Translation", etc.
    /// Used for source-based filtering and visual distinction in the UI.
    /// </summary>
    public LogSource LogSource { get; init; } = LogSource.App;

    /// <summary>
    /// Formatted single-line summary for UI display.
    /// </summary>
    public string DisplayText
    {
        get
        {
            var prefix = $"[{Timestamp:HH:mm:ss}] [{LevelTag}]";
            if (LogSource != LogSource.App)
                prefix += $" [{SourceTag}]";
            if (!string.IsNullOrEmpty(Category))
                prefix += $" [{Category}]";

            var text = $"{prefix} {Message}";

            if (DurationMs.HasValue)
                text += $" ({DurationMs.Value:F0}ms)";

            if (!string.IsNullOrEmpty(Exception))
                text += $" | {Exception}";

            return text;
        }
    }

    /// <summary>
    /// Short level tag (e.g. "INF", "WRN", "ERR", "DBG", "FTL", "VRB").
    /// </summary>
    public string LevelTag => Level switch
    {
        LogLevel.Verbose => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        _ => "???"
    };

    /// <summary>
    /// Short source tag for display in the UI (e.g. "APP", "CAD", "XLT").
    /// </summary>
    public string SourceTag => LogSource switch
    {
        LogSource.App => "APP",
        LogSource.CadPlugin => "CAD",
        LogSource.Translation => "XLT",
        _ => "???"
    };
}

/// <summary>
/// Log severity levels, mirroring Serilog's LogEventLevel.
/// </summary>
public enum LogLevel
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>
/// Indicates the origin subsystem of a log entry.
/// </summary>
public enum LogSource
{
    /// <summary>WPF application (default).</summary>
    App = 0,
    /// <summary>AutoCAD plugin (DwgTranslator.Cad).</summary>
    CadPlugin = 1,
    /// <summary>Translation pipeline (API calls, caching).</summary>
    Translation = 2
}
