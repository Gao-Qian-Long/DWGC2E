using System.Text.Json;

namespace DwgTranslator.Core.Logging;

/// <summary>
/// Parses log entries from JSON and plain-text formats.
/// </summary>
internal static class LogParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

    public static LogEntry? FromPlainText(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("[")) return null;

        try
        {
            var span = line.AsSpan();
            int idx = 1;

            int tsEnd = span.IndexOf(']');
            if (tsEnd < 0) return null;
            var tsStr = span[idx..tsEnd].ToString();
            if (!DateTime.TryParse(tsStr, out var timestamp))
                timestamp = DateTime.Now;
            idx = tsEnd + 1;

            if (idx >= span.Length || span[idx] != ' ') return null;
            idx++;
            if (idx >= span.Length || span[idx] != '[') return null;
            idx++;
            int levelEnd = span[idx..].IndexOf(']');
            if (levelEnd < 0) return null;
            var levelTag = span.Slice(idx, levelEnd).ToString();
            idx += levelEnd + 1;

            var level = ParseLevelTag(levelTag);
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
                        else { category = tag; }
                    }
                }
            }

            if (idx < span.Length && span[idx] == ' ') idx++;
            var message = span[idx..].ToString();

            string? exception = null;
            int pipeIdx = message.IndexOf(" | ");
            if (pipeIdx >= 0)
            {
                exception = message[(pipeIdx + 3)..];
                message = message[..pipeIdx];
            }

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
                Timestamp = timestamp, Level = level,
                Source = logSource.ToString(), LogSource = logSource,
                Category = category, Message = message,
                Exception = exception, DurationMs = durationMs
            };
        }
        catch { return null; }
    }

    public static LogLevel ParseLevelTag(string tag) => tag.ToUpperInvariant() switch
    {
        "VRB" or "VERBOSE" => LogLevel.Verbose,
        "DBG" or "DEBUG" => LogLevel.Debug,
        "INF" or "INFORMATION" or "INFO" => LogLevel.Information,
        "WRN" or "WARNING" or "WARN" => LogLevel.Warning,
        "ERR" or "ERROR" => LogLevel.Error,
        "FTL" or "FATAL" => LogLevel.Fatal,
        _ => LogLevel.Information
    };

    public static LogSource ParseSourceTag(string? tag) => tag?.ToUpperInvariant() switch
    {
        "CAD" => LogSource.CadPlugin,
        "XLT" => LogSource.Translation,
        "APP" => LogSource.App,
        _ => LogSource.App
    };
}
