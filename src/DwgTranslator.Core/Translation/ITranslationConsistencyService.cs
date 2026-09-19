namespace DwgTranslator.Core.Translation;

/// <summary>
/// Interface for translation consistency caching.
/// Ensures identical source text always receives the same translation.
/// </summary>
public interface ITranslationConsistencyService
{
    /// <summary>
    /// Number of cached entries.
    /// </summary>
    int CacheSize { get; }

    /// <summary>
    /// Try to find a cached translation for the given source text.
    /// </summary>
    /// <param name="sourceText">The source text to look up.</param>
    /// <param name="direction">
    /// 缓存作用域：语言方向（<c>ZH&gt;EN</c>），也可带术语表指纹（<c>ZH&gt;EN#g&lt;指纹&gt;</c>，
    /// 见 <c>EffectiveGlossary.ScopedDirection</c>）。带指纹时改过术语表的旧译文不会被命中。
    /// </param>
    /// <param name="translatedText">The cached translation if found.</param>
    /// <returns>True if a cached translation exists.</returns>
    bool TryGetMatch(string sourceText, string direction, out string? translatedText);

    /// <summary>
    /// Add a source->target mapping to the cache.
    /// </summary>
    /// <param name="direction">缓存作用域，语义与 <see cref="TryGetMatch"/> 一致。</param>
    void AddToCache(string sourceText, string translatedText, string direction);

    /// <summary>Remove an invalid or stale cached translation.</summary>
    /// <param name="direction">缓存作用域，语义与 <see cref="TryGetMatch"/> 一致。</param>
    void RemoveFromCache(string sourceText, string direction);

    /// <summary>
    /// Batch-add multiple entries to the cache.
    /// </summary>
    void AddBatchToCache(Dictionary<string, string> entries, string direction);

    /// <summary>
    /// Persist the in-memory cache to disk.
    /// </summary>
    void FlushCache();

    /// <summary>
    /// Load cache from disk.
    /// </summary>
    void LoadCache();

    /// <summary>
    /// Clear all cached entries.
    /// </summary>
    void Clear();

    /// <summary>
    /// Get a snapshot of all cached entries for export.
    /// </summary>
    Dictionary<string, string> ExportCache();
}
