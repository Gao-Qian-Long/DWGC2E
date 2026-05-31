using Autodesk.AutoCAD.EditorInput;

namespace DwgTranslator.Cad;

/// <summary>
/// Lightweight logger for the CAD plugin that writes to AutoCAD's command line
/// (via Editor.WriteMessage) and falls back to System.Diagnostics.Debug.
///
/// Usage: set Log.Editor once before engine code runs, in WritebackCommand.
/// All Log.Information/Warning/Error/Debug calls throughout the engine will
/// then appear on the AutoCAD command line for real-time user visibility.
/// </summary>
public static class Log
{
    /// <summary>
    /// Optional Editor for AutoCAD command-line output.
    /// Set in WritebackCommand.Execute() before the writeback engine runs.
    /// When null, output only goes to Debug.WriteLine.
    /// </summary>
    public static Editor? Editor { get; set; }

    /// <summary>
    /// Converts Serilog-style {Name} placeholders to string.Format-compatible {0},{1},...
    /// and formats the result. Falls back to concatenation on failure.
    /// </summary>
    private static string FormatMessage(string message, object?[] args)
    {
        if (args.Length == 0) return message;
        try
        {
            int idx = 0;
            var converted = System.Text.RegularExpressions.Regex.Replace(
                message, @"\{[A-Za-z_]\w*\}", _ => $"{{{idx++}}}");
            return idx > 0
                ? string.Format(converted, args)
                : $"{message} [{string.Join(", ", args.Select(a => a?.ToString() ?? "null"))}]";
        }
        catch { return $"{message} [{string.Join(", ", args.Select(a => a?.ToString() ?? "null"))}]"; }
    }

    private static void WriteLine(string level, string message)
    {
        var formatted = $"[DwgTranslator] [{level}] {message}";
        try { Editor?.WriteMessage($"\n{formatted}"); }
        catch { System.Diagnostics.Debug.WriteLine(formatted); }
        System.Diagnostics.Debug.WriteLine(formatted);
    }

    public static void Information(string message, params object?[] args) =>
        WriteLine("INFO", FormatMessage(message, args));

    public static void Warning(string message, params object?[] args) =>
        WriteLine("WARN", FormatMessage(message, args));

    public static void Warning(Exception? ex, string message, params object?[] args)
    {
        var msg = FormatMessage(message, args);
        if (ex != null) msg += $" [Exception: {ex.GetType().Name}: {ex.Message}]";
        WriteLine("WARN", msg);
    }

    public static void Error(string message, params object?[] args) =>
        WriteLine("ERROR", FormatMessage(message, args));

    public static void Error(Exception? ex, string message, params object?[] args)
    {
        var msg = FormatMessage(message, args);
        if (ex != null) msg += $" [Exception: {ex.GetType().Name}: {ex.Message}]";
        WriteLine("ERROR", msg);
    }

    public static void Debug(string message, params object?[] args) =>
        WriteLine("DEBUG", FormatMessage(message, args));
}