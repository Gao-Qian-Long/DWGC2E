using System.Text.RegularExpressions;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Translation;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements deduplicated concurrent translation pipeline.
/// Same PlainText is only translated once; results are mapped back to all entities with same text.
/// </summary>
public class TranslationService : ITranslationService
{
    private readonly IGlossaryService _glossaryService;
    private readonly IFormatCodeParser _formatCodeParser;
    private readonly IDeepSeekClient _deepSeekClient;
    private readonly string _systemPrompt;
    private readonly int _batchSize;
    private readonly int _maxRetryCount;
    private readonly int _maxConcurrency;
    private readonly ITranslationConsistencyService _consistencyService;

    public TranslationService(
        IGlossaryService glossaryService,
        IFormatCodeParser formatCodeParser,
        IDeepSeekClient deepSeekClient,
        string systemPrompt,
        int batchSize = 50,
        int maxRetryCount = 3,
        ITranslationConsistencyService? consistencyService = null,
        int maxConcurrency = 5)
    {
        _glossaryService = glossaryService;
        _formatCodeParser = formatCodeParser;
        _deepSeekClient = deepSeekClient;
        _systemPrompt = systemPrompt;
        _batchSize = batchSize;
        _maxRetryCount = maxRetryCount;
        _maxConcurrency = maxConcurrency;
        _consistencyService = consistencyService ?? new TranslationConsistencyService();
    }

    public async Task<List<TranslationPair>> TranslateBatchAsync(
        List<TextEntity> entities, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return await TranslateBatchWithProgressAsync(entities, sourceLanguage, targetLanguage, null, cancellationToken);
    }

    public async Task<List<TranslationPair>> TranslateBatchWithProgressAsync(
        List<TextEntity> entities, string sourceLanguage, string targetLanguage,
        IProgress<TranslationPair>? progress, CancellationToken cancellationToken = default)
    {
        var allResults = new List<TranslationPair>();

        var groups = entities
            .Where(e => !string.IsNullOrWhiteSpace(e.PlainText))
            .GroupBy(e => e.PlainText.Trim(), StringComparer.Ordinal)
            .ToList();

        int uniqueCount = groups.Count;
        int totalCount = entities.Count;
        Log.Information("Translation: {Unique} unique texts from {Total} entities (saved {Saved} API calls)",
            uniqueCount, totalCount, totalCount - uniqueCount);

        int concurrency = Math.Clamp(uniqueCount, 1, _maxConcurrency);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new List<Task>();
        var translationMap = new Dictionary<string, TranslationPair>(StringComparer.Ordinal);
        var mapLock = new object();
        int completed = 0;

        foreach (var group in groups)
        {
            var plainText = group.Key;
            var representative = group.First();

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var pair = await TranslateSingleAsync(representative, sourceLanguage, targetLanguage, cancellationToken);

                    lock (mapLock) { translationMap[plainText] = pair; }

                    int localDone = Interlocked.Increment(ref completed);
                    foreach (var entity in group)
                    {
                        var result = new TranslationPair
                        {
                            Handle = entity.Handle, SourceText = entity.PlainText,
                            TranslatedText = pair.TranslatedText, GlossaryHit = pair.GlossaryHit, Status = pair.Status
                        };
                        lock (mapLock) allResults.Add(result);
                        progress?.Report(result);
                    }

                    if (localDone % 10 == 0 || localDone == uniqueCount)
                        Log.Debug("Translation progress: {Done}/{Total} unique texts", localDone, uniqueCount);
                }
                finally { semaphore.Release(); }
            }, cancellationToken));
        }

        try { await Task.WhenAll(tasks); }
        catch (AggregateException ex) { Log.Warning(ex, "Some translation tasks failed"); }
        catch (OperationCanceledException) { /* user cancelled */ }
        _consistencyService.FlushCache();

        Log.Information("Translation complete: {Unique} unique -> {Total} total results", uniqueCount, allResults.Count);
        return allResults;
    }

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var (translated, _) = await TranslateWithMetadataAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        return translated;
    }

    private async Task<TranslationPair> TranslateSingleAsync(
        TextEntity entity, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entity.PlainText))
                return new TranslationPair { Handle = entity.Handle, Status = TranslationStatus.Skipped };

            if (TranslationFilter.IsNumericOnly(entity.PlainText))
            {
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = entity.PlainText, GlossaryHit = false, Status = TranslationStatus.Skipped
                };
            }

            if (TranslationFilter.ShouldSkipTranslation(entity.PlainText, sourceLanguage, targetLanguage))
            {
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = entity.PlainText, GlossaryHit = false, Status = TranslationStatus.Skipped
                };
            }

            if (_consistencyService.TryGetMatch(entity.PlainText, out var cached))
            {
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = RestoreFormatCodes(cached!, entity.RawText),
                    GlossaryHit = false, Status = TranslationStatus.Translated
                };
            }

            var (translated, glossaryHit) = await TranslateWithMetadataAsync(
                entity.PlainText, sourceLanguage, targetLanguage, ct);

            if (!glossaryHit && !string.IsNullOrEmpty(translated))
                _consistencyService.AddToCache(entity.PlainText, translated);

            var final = RestoreFormatCodes(translated, entity.RawText);

            return new TranslationPair
            {
                Handle = entity.Handle, SourceText = entity.PlainText,
                TranslatedText = final, GlossaryHit = glossaryHit,
                Status = string.IsNullOrEmpty(final) ? TranslationStatus.TranslationFailed : TranslationStatus.Translated
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Translation failed: {Handle}", entity.Handle);
            return new TranslationPair
            {
                Handle = entity.Handle, SourceText = entity.PlainText,
                TranslatedText = string.Empty, Status = TranslationStatus.TranslationFailed,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<(string TranslatedText, bool GlossaryHit)> TranslateWithMetadataAsync(
        string plainText, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return (string.Empty, false);

        var matches = _glossaryService.MatchTerms(plainText);
        var glossaryHit = matches.Count > 0;
        var withPlaceholders = _glossaryService.ReplaceWithPlaceholders(plainText, matches);
        var cleanText = _formatCodeParser.StripFormatCodes(withPlaceholders);
        if (string.IsNullOrWhiteSpace(cleanText)) return (plainText, glossaryHit);

        var restored = _glossaryService.RestorePlaceholders(cleanText, matches);
        if (glossaryHit && restored != cleanText && !restored.Contains("__GLOSSARY_"))
            return (restored, true);

        var translated = await TranslateWithRetryAsync(cleanText, sourceLanguage, targetLanguage, ct);
        translated = _glossaryService.RestorePlaceholders(translated, matches);
        return (TranslationFilter.CleanTranslationOutput(translated), glossaryHit);
    }

    private string RestoreFormatCodes(string translated, string rawText)
    {
        if (string.IsNullOrEmpty(rawText) || string.IsNullOrEmpty(translated)) return translated;

        var (_, template, codes) = _formatCodeParser.Parse(rawText);
        if (codes.Count == 0) return translated;

        var parts = Regex.Split(template, @"(__FMT_\d+__)");
        var sb = new System.Text.StringBuilder();
        bool textInserted = false;
        foreach (var part in parts)
        {
            if (Regex.IsMatch(part, @"^__FMT_\d+__$"))
                sb.Append(part);
            else if (!string.IsNullOrEmpty(part) && !textInserted)
            {
                sb.Append(translated);
                textInserted = true;
            }
        }
        var withPlaceholders = sb.ToString();
        var result = _formatCodeParser.Restore(withPlaceholders, codes);
        if (rawText.Contains("\\P"))
            result = result.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
        return result;
    }

    private async Task<string> TranslateWithRetryAsync(string text, string src, string tgt, CancellationToken ct)
    {
        Exception? last = null;
        var msg = BuildUserMessage(text, src, tgt);
        for (int i = 0; i <= _maxRetryCount; i++)
        {
            try
            {
                if (i > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)), ct);
                var result = await _deepSeekClient.ChatCompletionAsync(_systemPrompt, msg, ct);
                if (!string.IsNullOrWhiteSpace(result)) return TranslationFilter.CleanTranslationOutput(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; Log.Warning(ex, "Attempt {A}/{M}", i + 1, _maxRetryCount + 1); }
        }
        throw new InvalidOperationException($"Failed after {_maxRetryCount + 1} attempts", last);
    }

    private static string BuildUserMessage(string text, string src, string tgt)
    {
        var sn = src switch { "ZH" => "Chinese", "EN" => "English", "JA" => "Japanese", "KO" => "Korean", _ => src };
        var tn = tgt switch { "ZH" => "Chinese", "EN" => "English", "JA" => "Japanese", "KO" => "Korean", _ => tgt };
        return $"Translate the following CAD drawing text from {sn} to {tn}:\n\n---TEXT START---\n{text}\n---TEXT END---\n\nOutput ONLY the translated text. No explanations.";
    }
}
