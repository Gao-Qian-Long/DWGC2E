using System.Text;

namespace DwgTranslator.Core.Logging;

/// <summary>
/// Reads structured JSON-lines log files produced by the CAD plugin and feeds them
/// into the <see cref="ILogStore"/> so they appear in the App's LogViewerPanel.
///
/// Supports both the new .jsonl structured format and legacy .log plain-text format.
/// Runs a background poll to pick up new log entries from files that may still be
/// actively written by the CAD plugin.
/// </summary>
public sealed class CadLogReaderService : IDisposable
{
    private readonly ILogStore _logStore;
    private readonly string _logDirectory;
    private readonly System.Threading.Timer _pollTimer;
    private readonly HashSet<string> _processedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _filePositions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// How often to check for new log files/entries (in milliseconds).
    /// </summary>
    private const int PollIntervalMs = 3000;

    public CadLogReaderService(ILogStore logStore, string logDirectory)
    {
        _logStore = logStore;
        _logDirectory = logDirectory;
        _pollTimer = new System.Threading.Timer(OnPoll, null, PollIntervalMs, PollIntervalMs);

        // Initial scan
        ScanForNewEntries();
    }

    /// <summary>
    /// Scans the log directory for CAD plugin log files and loads new entries.
    /// </summary>
    public void ScanForNewEntries()
    {
        if (!Directory.Exists(_logDirectory)) return;

        try
        {
            // Get all CAD plugin log files, sorted by name (timestamp-based) ascending
            var files = Directory.GetFiles(_logDirectory, "cad_plugin_*")
                .Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
            {
                ReadNewEntries(file);
            }
        }
        catch
        {
            // Best-effort — don't crash the app if log reading fails
        }
    }

    private void ReadNewEntries(string filePath)
    {
        try
        {
            long lastPosition = _filePositions.GetValueOrDefault(filePath, 0);
            var fileInfo = new FileInfo(filePath);

            // If file hasn't grown, skip
            if (fileInfo.Length <= lastPosition) return;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                LogEntry? entry = null;

                // Try JSON-lines format first
                if (line.StartsWith('{'))
                {
                    entry = LogFormatter.FromJsonLine(line);
                }
                else
                {
                    // Legacy plain-text format
                    entry = LogFormatter.FromPlainText(line);
                }

                if (entry != null)
                {
                    // Ensure LogSource is CadPlugin
                    var cadEntry = new LogEntry
                    {
                        Timestamp = entry.Timestamp,
                        Level = entry.Level,
                        Category = entry.Category,
                        Message = entry.Message,
                        Exception = entry.Exception,
                        Source = "CAD",
                        LogSource = LogSource.CadPlugin,
                        CorrelationId = entry.CorrelationId,
                        DurationMs = entry.DurationMs
                    };
                    _logStore.Add(cadEntry);
                }
            }

            _filePositions[filePath] = stream.Position;
        }
        catch
        {
            // File may be locked by the CAD plugin — will retry on next poll
        }
    }

    private void OnPoll(object? state)
    {
        if (_disposed) return;
        try { ScanForNewEntries(); }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }
}