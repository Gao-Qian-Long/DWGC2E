using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwgTranslator.Core.Logging;

/// <summary>
/// Provides consistent log formatting for console/file output and structured JSON-lines.
/// </summary>
public static class LogFormatter
{
    public const string SerilogOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";

    public const string ConsoleOutputTemplate =
        "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Human-readable output: [HH:mm:ss.fff] [INF] [Category] Message
    /// </summary>
    public static string FormatReadable(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        sb.Append(entry.Timestamp.ToString("HH:mm:ss.fff"));
        sb.Append("] [");
        sb.Append(entry.LevelTag);
        sb.Append(']');

        if (entry.LogSource != LogSource.App)
        {
            sb.Append(" [");
            sb.Append(entry.SourceTag);
            sb.Append(']');
        }

        if (!string.IsNullOrEmpty(entry.Category))
        {
            sb.Append(" [");
            sb.Append(entry.Category);
            sb.Append(']');
        }

        sb.Append(' ');
        sb.Append(entry.Message);

        if (entry.DurationMs.HasValue)
        {
            sb.Append(" (");
            sb.Append(entry.DurationMs.Value.ToString("F0"));
            sb.Append("ms)");
        }

        if (!string.IsNullOrEmpty(entry.Exception))
        {
            sb.Append(" | ");
            sb.Append(entry.Exception);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Serializes a LogEntry to a single JSON line for structured file output.
    /// </summary>
    public static string ToJsonLine(LogEntry entry)
    {
        var obj = new LogEntryJson
        {
            Timestamp = entry.Timestamp.ToString("O"),
            Level = entry.LevelTag,
            Source = entry.SourceTag,
            Category = string.IsNullOrEmpty(entry.Category) ? null : entry.Category,
            Message = entry.Message,
            Exception = entry.Exception,
            CorrelationId = entry.CorrelationId,
            DurationMs = entry.DurationMs
        };
        return JsonSerializer.Serialize(obj, JsonOptions);
    }

    // Delegate to LogParser for deserialization
    public static LogEntry? FromJsonLine(string jsonLine) => LogParser.FromJsonLine(jsonLine);
    public static LogEntry? FromPlainText(string line) => LogParser.FromPlainText(line);
}

/// <summary>
/// JSON-serializable representation of a log entry for structured file output.
/// </summary>
internal sealed class LogEntryJson
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;
    [JsonPropertyName("level")] public string Level { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("exception")] public string? Exception { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("durationMs")] public double? DurationMs { get; set; }
}
