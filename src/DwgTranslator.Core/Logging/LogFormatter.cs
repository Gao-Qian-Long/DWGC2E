using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwgTranslator.Core.Logging;

/// <summary>
/// Provides consistent, enhanced log formatting for both console/file output
/// and structured JSON-lines log files. Shared by the App and CAD plugin.
/// </summary>
public static class LogFormatter
{
    /// <summary>
    /// Human-readable output template for console and file logs.
    /// Format: [HH:mm:ss.fff] [INF] [Category] Message (DurationMs) | Exception
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
    /// Serilog-style output template for the App's file sink.
    /// Compatible with {Timestamp}, {Level}, {SourceContext}, {Message}, {Exception} placeholders.
    /// </summary>
    public const string SerilogOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext:l}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Serilog-style output template for console (shorter, no date).
    /// </summary>
    public const string ConsoleOutputTemplate =
        "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// JSON serializer options for structured JSON-lines log output.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes a <see cref="LogEntry"/> to a single JSON line for structured file output.
    /// Used by the CAD plugin's file logger and can be consumed by the App's log reader.
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

    /// <summary>
    /// Deserializes a JSON line back to a <see cref="LogEntry"/>.
    /// Returns null if the line is not valid JSON or not a recognized log entry.
    /// </summary>
    public static LogEntry? FromJsonLine(string jsonLine)
    {
        try
        {
            var obj = JsonSerializer.Deserialize<LogEntryJson>(jsonLine, JsonOptions);
            if (obj == null || string.IsNullOrEmpty(obj.Message)) return null;

            return new LogEntry
            {
                Timestamp = DateTime.TryParse(obj.Timestamp, out var ts) ? ts : DateTime.Now,
                Level = ParseLevelTag(obj.Level),
                Source = obj.Source ?? string.Empty,
                LogSource = ParseSourceTag(obj.Source),
                Category = obj.Category ?? string.Empty,
                Message = obj.Message,
                Exception = obj.Exception,
                CorrelationId = obj.CorrelationId,
                DurationMs = obj.DurationMs
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse a plain-text log line (non-JSON) into a LogEntry.
    /// Supports the format: [HH:mm:ss.fff] [INF] [Category] Message
    /// </summary>
    public static LogEntry? FromPlainText(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            // Expected: [timestamp] [level] ... message
            if (!line.StartsWith("[")) return null;

            var span = line.AsSpan();
            int idx = 1;

            // Parse timestamp
            int tsEnd = span.IndexOf(']');
            if (tsEnd < 0) return null;
            var tsStr = span[idx..tsEnd].ToString();
            if (!DateTime.TryParse(tsStr, out var timestamp))
                timestamp = DateTime.Now;
            idx = tsEnd + 1;

            // Parse level tag
            if (idx >= span.Length || span[idx] != ' ') return null;
            idx++;
            if (idx >= span.Length || span[idx] != '[') return null;
            idx++;
            int levelEnd = span[idx..].IndexOf(']');
            if (levelEnd < 0) return null;
            var levelTag = span.Slice(idx, levelEnd).ToString();
            idx += levelEnd + 1;

            var level = ParseLevelTag(levelTag);

            // Check for source tag [APP], [CAD], [XLT]
            LogSource logSource = LogSource.App;
            string category = string.Empty;
            if (idx < span.Length && span[idx] == ' ')
            {
                idx++;
                if (idx < span.Length && span[idx] == '[')
                {
                    idx++;
                    int tagEnd = span[idx..].IndexOf(']');
                    if (tagEnd >= 0)
                    {
                        var tag = span.Slice(idx, tagEnd).ToString();
                        idx += tagEnd + 1;
                        if (tag is "CAD" or "APP" or "XLT")
                        {
                            logSource = ParseSourceTag(tag);
                            // Check for another bracket (category)
                            if (idx < span.Length && span[idx] == ' ')
                            {
                                idx++;
                                if (idx < span.Length && span[idx] == '[')
                                {
                                    idx++;
                                    int catEnd = span[idx..].IndexOf(']');
                                    if (catEnd >= 0)
                                    {
                                        category = span.Slice(idx, catEnd).ToString();
                                        idx += catEnd + 1;
                                    }
                                }
                            }
                        }
                        else
                        {
                            category = tag;
                        }
                    }
                }
            }

            // Remaining is the message (skip leading space)
            if (idx < span.Length && span[idx] == ' ') idx++;
            var message = span[idx..].ToString();

            // Split off exception if present
            string? exception = null;
            int pipeIdx = message.IndexOf(" | ");
            if (pipeIdx >= 0)
            {
                exception = message[(pipeIdx + 3)..];
                message = message[..pipeIdx];
            }

            // Split off duration if present
            double? durationMs = null;
            if (message.EndsWith(')'))
            {
                int parenIdx = message.LastIndexOf('(');
                if (parenIdx > 0)
                {
                    var durStr = message[(parenIdx + 1)..^1];
                    if (durStr.EndsWith("ms") && double.TryParse(durStr[..^2], out var dur))
                    {
                        durationMs = dur;
                        message = message[..parenIdx].TrimEnd();
                    }
                }
            }

            return new LogEntry
            {
                Timestamp = timestamp,
                Level = level,
                Source = logSource.ToString(),
                LogSource = logSource,
                Category = category,
                Message = message,
                Exception = exception,
                DurationMs = durationMs
            };
        }
        catch
        {
            return null;
        }
    }

    private static LogLevel ParseLevelTag(string tag) => tag.ToUpperInvariant() switch
    {
        "VRB" or "VERBOSE" => LogLevel.Verbose,
        "DBG" or "DEBUG" => LogLevel.Debug,
        "INF" or "INFORMATION" or "INFO" => LogLevel.Information,
        "WRN" or "WARNING" or "WARN" => LogLevel.Warning,
        "ERR" or "ERROR" => LogLevel.Error,
        "FTL" or "FATAL" => LogLevel.Fatal,
        _ => LogLevel.Information
    };

    private static LogSource ParseSourceTag(string? tag) => tag?.ToUpperInvariant() switch
    {
        "CAD" => LogSource.CadPlugin,
        "XLT" => LogSource.Translation,
        "APP" => LogSource.App,
        _ => LogSource.App
    };
}

/// <summary>
/// JSON-serializable representation of a log entry for structured file output.
/// </summary>
internal sealed class LogEntryJson
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("exception")]
    public string? Exception { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }
}