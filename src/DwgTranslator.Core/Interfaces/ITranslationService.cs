using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for translating text using DeepSeek API.
/// </summary>
public interface ITranslationService
{
    /// <summary>Translate a batch of text entities.</summary>
    Task<List<TranslationPair>> TranslateBatchAsync(
        List<TextEntity> entities,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>Translate a single text string.</summary>
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>Deduplicated concurrent translation with streaming progress.</summary>
    Task<List<TranslationPair>> TranslateBatchWithProgressAsync(
        List<TextEntity> entities,
        string sourceLanguage,
        string targetLanguage,
        IProgress<TranslationPair>? progress,
        CancellationToken cancellationToken = default);
}

/// <summary>Supplies stable, translation-semantic checkpoint data.</summary>
public interface ITranslationCheckpointContextProvider
{
    string GetCheckpointContext(string sourceLanguage, string targetLanguage);
}

/// <summary>Refreshes server-owned translation semantics before a checkpoint signature is computed.</summary>
public interface IAsyncTranslationCheckpointContextProvider
{
    Task PrepareCheckpointContextAsync(string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
}

/// <summary>Revalidate prescribed glossary text at the final CAD writeback boundary.</summary>
public interface IWritebackGlossaryProvider
{
    IReadOnlyList<GlossaryEntry> GetWritebackGlossary(string sourceLanguage, string targetLanguage);
}
