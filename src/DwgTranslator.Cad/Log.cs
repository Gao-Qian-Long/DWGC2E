using Autodesk.AutoCAD.EditorInput;
using System.Text.Json;

namespace DwgTranslator.Cad;

/// <summary>
/// Lightweight structured logger for the CAD plugin.
///
/// Outputs to:
///   1. AutoCAD command line (via Editor.WriteMessage) — real-time user visibility
///   2. System.Diagnostics.Debug — VS output window
///   3. Structured JSON-lines file log with rotation — persisted for later retrieval by the App UI
///
/// Usage: set Log.Editor once before engine code runs, in WritebackCommand.
/// All Log.Information/Warning/Error/Debug calls throughout the engine will
/// then appear on the AutoCAD command line for real-time user visibility.
///
/// File rotation: max 5 MB per file, keep 5 most recent files.
/// </summary>
public static class Log
{
    private static readonly object FileLock = new();
    private static StreamWriter? _fileWriter;
    private static string? _logDirectory;
    private static string? _currentLogFilePath;
    private static long _currentFileSize;

    // Rotation settings
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB per file
    private const int MaxRetainedFiles = 5;

    // JSON serializer options
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Minimum log level to output. Entries below this level are silently discarded.
    /// Default: Debug (all levels).
    /// </summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Optional Editor for AutoCAD command-line output.
    /// Set in WritebackCommand.Execute() before the writeback engine runs.
    /// When null, output only goes to Debug.WriteLine and file.
    /// </summary>
    public static Editor? Editor { get; set; }

    /// <summary>
    /// Initialize file logging with rotation. Call once at plugin startup.
    /// Creates structured JSON-lines log files that the App can read.
    /// </summary>
    public static void InitFileLogging(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logDirectory = logDirectory;
            RotateOldFiles();
            CreateNewLogFile();
        }
        catch
        {
            // File logging is optional — ignore failures
        }
    }

    /// <summary>
    /// Close the file writer if open.
    /// </summary>
    public static void CloseFileLogging()
    {
        lock (FileLock)
        {
            _fileWriter?.Flush();
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }

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

    /// <summary>
    /// Core write method. Outputs to Editor, Debug, and optional file (structured JSON-lines).
    /// </summary>
    private static void WriteLine(LogLevel level, string category, string message, string? exception = null)
    {
        if (level < MinimumLevel) return;

        var timestamp = DateTime.Now;
        var levelTag = level switch
        {
            LogLevel.Verbose => "VRB",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Fatal => "FTL",
            _ => "???"
        };

        // Human-readable format for Editor and Debug output
        var readable = string.IsNullOrEmpty(category)
            ? $"[{timestamp:HH:mm:ss.fff}] [{levelTag}] {message}"
            : $"[{timestamp:HH:mm:ss.fff}] [{levelTag}] [{category}] {message}";
        if (!string.IsNullOrEmpty(exception))
            readable += $" | {exception}";

        // Output to AutoCAD command line
        try { Editor?.WriteMessage($"\n[DwgTranslator] {readable}"); }
        catch { System.Diagnostics.Debug.WriteLine(readable); }

        // Output to Debug (VS output window)
        System.Diagnostics.Debug.WriteLine(readable);

        // Output to file as structured JSON line
        WriteJsonLine(timestamp, levelTag, category, message, exception);
    }

    /// <summary>
    /// Writes a structured JSON-lines entry to the log file with rotation check.
    /// </summary>
    private static void WriteJsonLine(DateTime timestamp, string levelTag, string category, string message, string? exception)
    {
        if (_logDirectory == null) return;
        lock (FileLock)
        {
            try
            {
                // Check if rotation is needed
                if (_currentFileSize > MaxFileSizeBytes || _fileWriter == null)
                {
                    _fileWriter?.Flush();
                    _fileWriter?.Dispose();
                    _fileWriter = null;
                    RotateOldFiles();
                    CreateNewLogFile();
                }

                if (_fileWriter == null) return;

                var json = JsonSerializer.Serialize(new
                {
                    timestamp = timestamp.ToString("O"),
                    level = levelTag,
                    source = "CAD",
                    category = string.IsNullOrEmpty(category) ? null : category,
                    message,
                    exception
                }, JsonOptions);

                _fileWriter.WriteLine(json);
                _currentFileSize += System.Text.Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
            }
            catch { /* ignore file write errors */ }
        }
    }

    /// <summary>
    /// Creates a new log file and initializes the writer.
    /// </summary>
    private static void CreateNewLogFile()
    {
        try
        {
            if (_logDirectory == null) return;
            _currentLogFilePath = Path.Combine(_logDirectory, $"cad_plugin_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            _fileWriter = new StreamWriter(_currentLogFilePath, append: true, encoding: System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
            _currentFileSize = 0;
        }
        catch
        {
            _fileWriter = null;
        }
    }

    /// <summary>
    /// Rotates old log files, keeping only the most recent MaxRetainedFiles.
    /// Also cleans up legacy .log files from the previous format.
    /// </summary>
    private static void RotateOldFiles()
    {
        try
        {
            if (_logDirectory == null || !Directory.Exists(_logDirectory)) return;

            // Get all CAD plugin log files (both old .log and new .jsonl)
            var files = Directory.GetFiles(_logDirectory, "cad_plugin_*")
                .Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            // Delete files beyond retention limit
            for (int i = MaxRetainedFiles; i < files.Count; i++)
            {
                try { File.Delete(files[i]); }
                catch { /* ignore — file may be in use */ }
            }
        }
        catch
        {
            // Rotation is best-effort
        }
    }

    // ───────────────────────── Public API (backwards-compatible) ─────────────────────────

    public static void Information(string message, params object?[] args) =>
        WriteLine(LogLevel.Information, string.Empty, FormatMessage(message, args));

    public static void Information(string category, string message, params object?[] args) =>
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

    public static void Debug(string category, string message, params object?[] args) =>
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

    /// <summary>
    /// Log severity levels for the CAD plugin.
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
}