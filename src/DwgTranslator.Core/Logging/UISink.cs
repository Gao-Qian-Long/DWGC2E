using Serilog.Core;
using Serilog.Events;

namespace DwgTranslator.Core.Logging;

/// <summary>
/// Custom Serilog sink that bridges Serilog log events to our <see cref="ILogStore"/>
/// for real-time UI display. Registered alongside file and console sinks.
/// </summary>
public sealed class UISink : ILogEventSink
{
    private readonly ILogStore _store;

    public UISink(ILogStore store)
    {
        _store = store;
    }

    public void Emit(LogEvent logEvent)
    {
        var category = logEvent.Properties.TryGetValue("SourceContext", out var ctx)
            ? ctx.ToString().Trim('"')
            : string.Empty;

        // Infer LogSource from the category/namespace
        var logSource = InferLogSource(category);

        // Extract correlation ID if present
        string? correlationId = null;
        if (logEvent.Properties.TryGetValue("CorrelationId", out var corrId))
            correlationId = corrId.ToString().Trim('"');

        // Extract duration if present
        double? durationMs = null;
        if (logEvent.Properties.TryGetValue("Elapsed", out var elapsed)
            && double.TryParse(elapsed.ToString(), out var dur))
        {
            durationMs = dur;
        }

        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp.LocalDateTime,
            Level = MapLevel(logEvent.Level),
            Category = category,
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            LogSource = logSource,
            CorrelationId = correlationId,
            DurationMs = durationMs
        };

        _store.Add(entry);
    }

    /// <summary>
    /// Infers the log source from the Serilog SourceContext (typically the namespace).
    /// </summary>
    private static LogSource InferLogSource(string category)
    {
        if (string.IsNullOrEmpty(category)) return LogSource.App;
        if (category.Contains("Translation", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
            return LogSource.Translation;
        if (category.Contains(".Cad", StringComparison.OrdinalIgnoreCase))
            return LogSource.CadPlugin;
        return LogSource.App;
    }

    private static LogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Verbose,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Fatal,
        _ => LogLevel.Information
    };
}
