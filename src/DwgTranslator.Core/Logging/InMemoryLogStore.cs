namespace DwgTranslator.Core.Logging;

/// <summary>
/// Thread-safe in-memory ring-buffer log store for real-time UI display.
/// Keeps the most recent entries and discards oldest when capacity is reached.
/// </summary>
public sealed class InMemoryLogStore : ILogStore
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly int _capacity;

    public event Action<LogEntry>? EntryAdded;

    public InMemoryLogStore(int capacity = 2000)
    {
        _capacity = capacity;
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public void Add(LogEntry entry)
    {
        if (entry.Level < MinimumLevel) return;

        lock (_lock)
        {
            if (_entries.Count >= _capacity)
            {
                // Remove oldest 10% to avoid frequent resizing
                int toRemove = _capacity / 10;
                _entries.RemoveRange(0, toRemove);
            }
            _entries.Add(entry);
        }

        // Fire event (caller is responsible for marshaling to UI thread if needed)
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 200)
    {
        lock (_lock)
        {
            var take = Math.Min(count, _entries.Count);
            var result = new List<LogEntry>(take);
            for (int i = _entries.Count - 1; i >= _entries.Count - take; i--)
                result.Add(_entries[i]);
            return result;
        }
    }

    public IReadOnlyList<LogEntry> GetFiltered(LogLevel minLevel, string? categoryFilter = null, int count = 200, LogSource? sourceFilter = null)
    {
        lock (_lock)
        {
            var result = new List<LogEntry>();
            for (int i = _entries.Count - 1; i >= 0 && result.Count < count; i--)
            {
                var e = _entries[i];
                if (e.Level < minLevel) continue;
                if (sourceFilter.HasValue && e.LogSource != sourceFilter.Value) continue;
                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !e.Category.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase) &&
                    !e.Message.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase) &&
                    !e.Source.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(e);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }
}
