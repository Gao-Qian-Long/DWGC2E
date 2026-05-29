using Serilog;
using System.Text.Json;

namespace DwgTranslator.Core.Translation;

/// <summary>
/// Provides translation consistency by caching source->target mappings.
/// Ensures identical source text always receives the same translation,
/// reducing API costs and improving consistency across the drawing.
/// </summary>
public class TranslationConsistencyService
{
    private readonly Dictionary<string, string> _cache;
    private readonly string _cacheFilePath;
    private readonly object _lock = new();

    /// <summary>
    /// Number of cached entries.
    /// </summary>
    public int CacheSize
    {
        get { lock (_lock) return _cache.Count; }
    }

    /// <summary>
    /// Creates a new consistency service with optional persistent cache file.
    /// </summary>
    /// <param name="cacheFilePath">Path to JSON cache file for persistence. If empty, uses in-memory only.</param>
    public TranslationConsistencyService(string cacheFilePath = "")
    {
        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        _cacheFilePath = cacheFilePath;

        if (!string.IsNullOrEmpty(_cacheFilePath))
        {
            LoadCache();
        }
    }

    /// <summary>
    /// Try to find a cached translation for the given source text.
    /// </summary>
    /// <param name="sourceText">The source text to look up.</param>
    /// <param name="translatedText">The cached translation if found.</param>
    /// <returns>True if a cached translation exists.</returns>
    public bool TryGetMatch(string sourceText, out string? translatedText)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            translatedText = null;
            return false;
        }

        var normalized = NormalizeForCache(sourceText);

        lock (_lock)
        {
            if (_cache.TryGetValue(normalized, out var cached))
            {
                translatedText = cached;
                return true;
            }
        }

        translatedText = null;
        return false;
    }

    /// <summary>
    /// Add a source->target mapping to the cache.
    /// </summary>
    /// <param name="sourceText">The original text.</param>
    /// <param name="translatedText">The translated text.</param>
    public void AddToCache(string sourceText, string translatedText)
    {
        if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translatedText))
            return;

        var normalized = NormalizeForCache(sourceText);

        lock (_lock)
        {
            if (!_cache.ContainsKey(normalized))
            {
                _cache[normalized] = translatedText;
                Log.Debug("Translation cached: '{Src}' -> '{Tgt}'",
                    sourceText.Length > 40 ? sourceText[..40] + "..." : sourceText,
                    translatedText.Length > 40 ? translatedText[..40] + "..." : translatedText);
            }
            else
            {
                // Verify consistency: if different, log warning
                var existing = _cache[normalized];
                if (!string.Equals(existing, translatedText, StringComparison.Ordinal))
                {
                    Log.Warning("Translation inconsistency detected for '{Src}': existing='{Old}' vs new='{New}'",
                        sourceText.Length > 30 ? sourceText[..30] + "..." : sourceText,
                        existing, translatedText);
                }
            }
        }
    }

    /// <summary>
    /// Batch-add multiple entries to the cache.
    /// </summary>
    public void AddBatchToCache(Dictionary<string, string> entries)
    {
        lock (_lock)
        {
            foreach (var kvp in entries)
            {
                var normalized = NormalizeForCache(kvp.Key);
                _cache[normalized] = kvp.Value;
            }
        }
        Log.Information("Batch-added {Count} translation cache entries", entries.Count);
    }

    /// <summary>
    /// Persist the in-memory cache to disk.
    /// </summary>
    public void FlushCache()
    {
        if (string.IsNullOrEmpty(_cacheFilePath))
            return;

        try
        {
            Dictionary<string, string> snapshot;
            lock (_lock)
            {
                snapshot = new Dictionary<string, string>(_cache);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_cacheFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist translation cache to {Path}", _cacheFilePath);
        }
    }

    /// <summary>
    /// Load cache from disk.
    /// </summary>
    private void LoadCache()
    {
        if (string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_cacheFilePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (entries != null)
            {
                lock (_lock)
                {
                    foreach (var kvp in entries)
                    {
                        _cache[kvp.Key] = kvp.Value;
                    }
                }
                Log.Information("Loaded {Count} translation cache entries from {Path}", entries.Count, _cacheFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load translation cache from {Path}", _cacheFilePath);
        }
    }

    /// <summary>
    /// Clear all cached entries.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
        Log.Information("Translation cache cleared");
    }

    /// <summary>
    /// Get a snapshot of all cached entries for export.
    /// </summary>
    public Dictionary<string, string> ExportCache()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_cache);
        }
    }

    /// <summary>
    /// Normalize source text for cache lookup.
    /// Trim whitespace and normalize line endings for consistent matching.
    /// </summary>
    private static string NormalizeForCache(string text)
    {
        return text.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
    }
}