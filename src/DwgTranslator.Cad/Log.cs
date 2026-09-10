#if GSTARCAD
using Gssoft.Gscad.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif

namespace DwgTranslator.Cad;

/// <summary>
/// Lightweight structured logger for the CAD plugin.
/// Outputs to AutoCAD command line, Debug.WriteLine, and structured JSON-lines file.
/// </summary>
public static class Log
{
    /// <summary>
    /// Minimum log level to output. Entries below this level are silently discarded.
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Optional Editor for AutoCAD command-line output.
    /// </summary>
    public static Editor? Editor { get; set; }

    /// <summary>
    /// Whether to output logs to the AutoCAD command line (only level >= Warning).
    /// </summary>
    public static bool OutputToCommandLine { get; set; } = false;

    public static void InitFileLogging(string logDirectory) => CadFileLogger.Init(logDirectory);
    public static void CloseFileLogging() => CadFileLogger.Close();

    private static void WriteLine(LogLevel level, string category, string message, string? exception = null)
    {
        if (level < MinimumLevel) return;

        var timestamp = DateTime.Now;
        var levelTag = level switch
        {
            LogLevel.Verbose => "VRB", LogLevel.Debug => "DBG",
            LogLevel.Information => "INF", LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR", LogLevel.Fatal => "FTL",
            _ => "???"
        };

        var readable = string.IsNullOrEmpty(category)
            ? $"[{timestamp:HH:mm:ss.fff}] [{levelTag}] {message}"
            : $"[{timestamp:HH:mm:ss.fff}] [{levelTag}] [{category}] {message}";
        if (!string.IsNullOrEmpty(exception))
            readable += $" | {exception}";

        if (OutputToCommandLine && level >= LogLevel.Warning)
        {
            try { Editor?.WriteMessage($"\n[DwgTranslator] {readable}"); }
            catch { System.Diagnostics.Debug.WriteLine(readable); }
        }

        System.Diagnostics.Debug.WriteLine(readable);
        CadFileLogger.Write(timestamp, levelTag, category, message, exception);
    }

    private static string FormatMessage(string message, object?[] args)
    {
        if (args.Length == 0) return message;
        try
        {
            int idx = 0;
            // Keep an optional format specifier ({OldW:F3}) so string.Format still applies
            // it. The previous pattern only matched bare {Name}, so any template using a
            // specifier failed to convert, threw inside string.Format, and fell through to
            // the catch below -- emitting the raw template plus an argument dump instead of
            // a readable line. That silently destroyed envelope-fit diagnostics.
            var converted = System.Text.RegularExpressions.Regex.Replace(
                message, @"\{[A-Za-z_]\w*(:[^}]*)?\}", m => $"{{{idx++}{m.Groups[1].Value}}}");
            return idx > 0
                ? string.Format(converted, args)
                : $"{message} [{string.Join(", ", args.Select(a => a?.ToString() ?? "null"))}]";
        }
        catch { return $"{message} [{string.Join(", ", args.Select(a => a?.ToString() ?? "null"))}]"; }
    }

    public static void Information(string message, params object?[] args) =>
        WriteLine(LogLevel.Information, string.Empty, FormatMessage(message, args));

    // Deliberately a distinct name. A second overload taking (category, message, args) is
    // ambiguous for a call like Information(template, someString, 1.0, 2.0): the compiler
    // binds it to (category, message, args), so the first argument became the category and
    // the message became the string argument -- every such line was logged as
    // "handle [numbers]" instead of the formatted message.
    public static void InformationCategorized(string category, string message, params object?[] args) =>
        WriteLine(LogLevel.Information, category, FormatMessage(message, args));

    public static void Warning(string message, params object?[] args) =>
        WriteLine(LogLevel.Warning, string.Empty, FormatMessage(message, args));

    public static void Warning(Exception? ex, string message, params object?[] args)
    {
        var msg = FormatMessage(message, args);
        var exStr = ex != null ? $"{ex.GetType().Name}: {ex.Message}" : null;
        WriteLine(LogLevel.Warning, string.Empty, msg, exStr);
    }

    public static void Error(string message, params object?[] args) =>
        WriteLine(LogLevel.Error, string.Empty, FormatMessage(message, args));

    public static void Error(Exception? ex, string message, params object?[] args)
    {
        var msg = FormatMessage(message, args);
        var exStr = ex != null ? $"{ex.GetType().Name}: {ex.Message}" : null;
        WriteLine(LogLevel.Error, string.Empty, msg, exStr);
    }

    public static void Debug(string message, params object?[] args) =>
        WriteLine(LogLevel.Debug, string.Empty, FormatMessage(message, args));

    /// <inheritdoc cref="InformationCategorized"/>
    public static void DebugCategorized(string category, string message, params object?[] args) =>
        WriteLine(LogLevel.Debug, category, FormatMessage(message, args));

    public static void Verbose(string message, params object?[] args) =>
        WriteLine(LogLevel.Verbose, string.Empty, FormatMessage(message, args));

    public static void Fatal(string message, params object?[] args) =>
        WriteLine(LogLevel.Fatal, string.Empty, FormatMessage(message, args));

    public static void Fatal(Exception? ex, string message, params object?[] args)
    {
        var msg = FormatMessage(message, args);
        var exStr = ex != null ? $"{ex.GetType().Name}: {ex.Message}" : null;
        WriteLine(LogLevel.Fatal, string.Empty, msg, exStr);
    }

    public enum LogLevel { Verbose = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Fatal = 5 }
}
