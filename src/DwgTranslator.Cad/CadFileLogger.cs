using System;
using System.IO;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace DwgTranslator.Cad;

/// <summary>
/// File-based JSON-lines log writer with rotation support for the CAD plugin.
/// </summary>
internal static class CadFileLogger
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxRetainedFiles = 5;

    private static readonly object FileLock = new();
#if NETFRAMEWORK
    private static readonly JavaScriptSerializer JsonSerializer = new();
#else
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
#endif

    private static StreamWriter? _fileWriter;
    private static string? _logDirectory;
    private static string? _currentLogFilePath;
    private static long _currentFileSize;

    /// <summary>
    /// Initialize file logging with rotation. Call once at plugin startup.
    /// </summary>
    public static void Init(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logDirectory = logDirectory;
            RotateOldFiles();
            CreateNewLogFile();
        }
        catch { }
    }

    /// <summary>
    /// Close the file writer if open.
    /// </summary>
    public static void Close()
    {
        lock (FileLock)
        {
            _fileWriter?.Flush();
            _fileWriter?.Dispose();
            _fileWriter = null;
        }
    }

    /// <summary>
    /// Writes a structured JSON-lines entry to the log file with rotation check.
    /// </summary>
    public static void Write(DateTime timestamp, string levelTag, string category, string message, string? exception)
    {
        if (_logDirectory == null) return;
        lock (FileLock)
        {
            try
            {
                if (_currentFileSize > MaxFileSizeBytes || _fileWriter == null)
                {
                    _fileWriter?.Flush();
                    _fileWriter?.Dispose();
                    _fileWriter = null;
                    RotateOldFiles();
                    CreateNewLogFile();
                }

                if (_fileWriter == null) return;

                var entry = new
                {
                    timestamp = timestamp.ToString("O"),
                    level = levelTag,
                    source = "CAD",
                    category = string.IsNullOrEmpty(category) ? null : category,
                    message,
                    exception
                };
#if NETFRAMEWORK
                var json = JsonSerializer.Serialize(entry);
#else
                var json = System.Text.Json.JsonSerializer.Serialize(entry, JsonOptions);
#endif

                _fileWriter.WriteLine(json);
                _currentFileSize += System.Text.Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
            }
            catch { }
        }
    }

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

    private static void RotateOldFiles()
    {
        try
        {
            if (_logDirectory == null || !Directory.Exists(_logDirectory)) return;

            var files = Directory.GetFiles(_logDirectory, "cad_plugin_*")
                .Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            for (int i = MaxRetainedFiles; i < files.Count; i++)
            {
                try { File.Delete(files[i]); }
                catch { }
            }
        }
        catch { }
    }
}
