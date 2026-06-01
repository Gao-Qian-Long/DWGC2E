namespace DwgTranslator.Core.Logging;

/// <summary>
/// Abstraction for log storage. Implementations may be in-memory (for UI display)
/// or file-backed (for persistence). The App registers one global instance in DI.
/// </summary>
public interface ILogStore
{
    /// <summary>
    /// Fired on the UI thread when a new log entry is added.
    /// Subscribers should bind to this for real-time log display.
    /// </summary>
    event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// Add a log entry to the store.
    /// </summary>
    void Add(LogEntry entry);

    /// <summary>
    /// Get recent log entries, newest first. Limited to <paramref name="count"/> items.
    /// </summary>
    IReadOnlyList<LogEntry> GetRecent(int count = 200);

    /// <summary>
    /// Get log entries filtered by minimum level, optional category/message text filter, and optional source filter.
    /// </summary>
    IReadOnlyList<LogEntry> GetFiltered(LogLevel minLevel, string? categoryFilter = null, int count = 200, LogSource? sourceFilter = null);

    /// <summary>
    /// Clear all stored entries.
    /// </summary>
    void Clear();

    /// <summary>
    /// Total number of entries currently stored.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Minimum log level that will be stored. Entries below this level are discarded.
    /// </summary>
    LogLevel MinimumLevel { get; set; }
}
