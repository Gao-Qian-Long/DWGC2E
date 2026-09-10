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
        int maxConcurrency = 12)
    {
        _glossaryService = glossaryService;
        _formatCodeParser = formatCodeParser;
        _deepSeekClient = deepSeekClient;
        _systemPrompt = systemPrompt;
        _batchSize = Math.Max(1, batchSize);
        _maxRetryCount = maxRetryCount;
        // Cap concurrency by both configured max and batch size so large batchSize
        // settings can raise parallelism without exceeding the hard limit.
        _maxConcurrency = Math.Clamp(maxConcurrency > 0 ? maxConcurrency : 12, 1, 20);
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

        // Use batch size as a soft concurrency budget: allow up to min(unique, maxConcurrency, batchSize).
        int concurrency = Math.Clamp(Math.Min(uniqueCount, _batchSize), 1, _maxConcurrency);
        Log.Information("Translation worker pool: {Concurrency} concurrent requests (configured max {ConfiguredMax})",
            concurrency, _maxConcurrency);
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
                        // Deduplication reuses only the plain translated content. Each entity
                        // must restore its own RawText format template independently.
                        var entityTranslation = pair.Status == TranslationStatus.Translated
                            ? RestoreFormatCodes(targetLanguage == "EN"
                                ? CadLabelCompactor.Compact(entity.PlainText,pair.TranslatedText)
                                : pair.TranslatedText, entity.RawText)
                            : pair.TranslatedText;
                        var result = new TranslationPair
                        {
                            Handle = entity.Handle, SourceText = entity.PlainText,
                            SourceFilePath = entity.SourceFilePath,
                            TranslatedText = entityTranslation, GlossaryHit = pair.GlossaryHit,
                            Status = string.IsNullOrEmpty(entityTranslation) && pair.Status == TranslationStatus.Translated
                                ? TranslationStatus.TranslationFailed
                                : pair.Status
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

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex, "Some translation tasks failed");
        }
        catch (OperationCanceledException)
        {
            _consistencyService.FlushCache();
            throw;
        }
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
                if (TranslationQualityValidator.IsAcceptable(
                    entity.PlainText, cached!, sourceLanguage, targetLanguage))
                {
                    return new TranslationPair
                    {
                        Handle = entity.Handle, SourceText = entity.PlainText,
                        TranslatedText = cached!,
                        GlossaryHit = false, Status = TranslationStatus.Translated
                    };
                }

                // Older versions cached echoed source text as a successful translation.
                // Remove it and request a fresh translation instead of propagating it.
                _consistencyService.RemoveFromCache(entity.PlainText);
            }

            var (translated, glossaryHit) = await TranslateWithMetadataAsync(
                entity.PlainText, sourceLanguage, targetLanguage, ct);

            if (!TranslationQualityValidator.IsAcceptable(
                    entity.PlainText, translated, sourceLanguage, targetLanguage))
                throw new InvalidDataException("Translation response still contains source-language text");

            if (!glossaryHit && !string.IsNullOrEmpty(translated))
                _consistencyService.AddToCache(entity.PlainText, translated);

            return new TranslationPair
            {
                Handle = entity.Handle, SourceText = entity.PlainText,
                TranslatedText = translated, GlossaryHit = glossaryHit,
                Status = string.IsNullOrEmpty(translated) ? TranslationStatus.TranslationFailed : TranslationStatus.Translated
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

        // Only short-circuit when glossary fully covers the whole phrase
        // (no residual human language left to translate). Partial matches
        // (e.g. "螺栓" inside a longer sentence) must still go through the API
        // so the surrounding words get translated with placeholders preserved.
        var residual = _glossaryService.RestorePlaceholders(cleanText, matches);
        if (glossaryHit && IsFullyGlossaryCovered(plainText, residual, matches))
            return (residual, true);

        var translated = await TranslateWithRetryAsync(cleanText, sourceLanguage, targetLanguage, ct);
        translated = _glossaryService.RestorePlaceholders(translated, matches);
        return (TranslationFilter.CleanTranslationOutput(translated), glossaryHit);
    }

    /// <summary>
    /// True when glossary replacement accounts for essentially the entire source
    /// phrase (only punctuation/whitespace remains outside glossary terms).
    /// </summary>
    private static bool IsFullyGlossaryCovered(string original, string restored, List<GlossaryMatch> matches)
    {
        if (matches.Count == 0) return false;
        if (restored.Contains("__GLOSSARY_", StringComparison.Ordinal)) return false;

        // Strip glossary targets and see if anything meaningful remains in the original.
        var residual = original;
        foreach (var match in matches.OrderByDescending(m => m.SourceTerm.Length))
        {
            residual = residual.Replace(match.SourceTerm, " ", StringComparison.Ordinal);
        }

        residual = residual.Trim();
        if (string.IsNullOrEmpty(residual)) return true;

        // Only punctuation / symbols / digits left => fully covered
        return residual.All(c =>
            char.IsWhiteSpace(c) ||
            char.IsDigit(c) ||
            char.IsPunctuation(c) ||
            char.IsSymbol(c) ||
            c is '×' or '±' or '°' or '#' or '%');
    }

    public string RestoreFormatCodes(string translated, string rawText)
    {
        if (string.IsNullOrEmpty(rawText) || string.IsNullOrEmpty(translated)) return translated;

        var (_, template, codes) = _formatCodeParser.Parse(rawText);
        if (codes.Count == 0) return translated;

        // Split template into format placeholders and text segments.
        // Multi-segment MText (code + text + code + text) must preserve ALL
        // text islands. Previous logic only inserted into the first non-empty
        // segment and dropped later ones.
        var parts = Regex.Split(template, @"(__FMT_\d+__)");
        var textSegmentIndexes = new List<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (!Regex.IsMatch(parts[i], @"^__FMT_\d+__$") && !string.IsNullOrEmpty(parts[i]))
                textSegmentIndexes.Add(i);
        }

        if (textSegmentIndexes.Count == 0)
        {
            // Template is pure format codes - append translation at the end.
            var pure = string.Concat(parts) + translated;
            var pureResult = _formatCodeParser.Restore(pure, codes);
            if (rawText.Contains("\\P"))
                pureResult = pureResult.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
            return pureResult;
        }

        if (textSegmentIndexes.Count == 1)
        {
            parts[textSegmentIndexes[0]] = translated;
        }
        else
        {
            // Multiple text islands: prefer putting the full translation into the
            // longest original text island (usually the main content). Other
            // islands that were pure format/noise keep their stripped emptiness.
            // If the original islands look like multi-line content split by \P,
            // distribute translated lines across them when counts match.
            var originalSegments = textSegmentIndexes.Select(i => parts[i]).ToList();
            var translatedLines = translated
                .Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n');

            if (translatedLines.Length == originalSegments.Count)
            {
                for (int i = 0; i < textSegmentIndexes.Count; i++)
                    parts[textSegmentIndexes[i]] = translatedLines[i];
            }
            else if (originalSegments.Count == 2 &&
                !TranslationQualityValidator.ContainsCjk(originalSegments[0]) &&
                !string.IsNullOrWhiteSpace(originalSegments[0]) &&
                translated.TrimStart().StartsWith(originalSegments[0].Trim(),StringComparison.Ordinal))
            {
                // Preserve an unchanged model identifier on its original first line.
                // Putting everything in the longer second island creates a leading
                // blank paragraph and needlessly halves the rendered font height.
                var prefix=originalSegments[0].Trim();
                parts[textSegmentIndexes[0]]=prefix;
                parts[textSegmentIndexes[1]]=translated.TrimStart().Substring(prefix.Length).TrimStart();
            }
            else
            {
                int longestIdx = 0;
                int longestLen = 0;
                for (int i = 0; i < originalSegments.Count; i++)
                {
                    if (originalSegments[i].Length > longestLen)
                    {
                        longestLen = originalSegments[i].Length;
                        longestIdx = i;
                    }
                }

                for (int i = 0; i < textSegmentIndexes.Count; i++)
                    parts[textSegmentIndexes[i]] = i == longestIdx ? translated : string.Empty;
            }
        }

        var withPlaceholders = string.Concat(parts);
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
                result = TranslationFilter.CleanTranslationOutput(result);
                if (TranslationQualityValidator.IsAcceptable(text, result, src, tgt, allowPlaceholders: true)) return result;

                last = new InvalidDataException("Model echoed or retained source-language text");
                Log.Warning("Rejected incomplete translation attempt {A}/{M}: '{Result}'",
                    i + 1, _maxRetryCount + 1, TruncateForLog(result));
                msg = BuildCorrectionMessage(text, src, tgt, result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; Log.Warning(ex, "Attempt {A}/{M}", i + 1, _maxRetryCount + 1); }
        }
        throw new InvalidOperationException($"Failed after {_maxRetryCount + 1} attempts", last);
    }

    private static string BuildUserMessage(string text, string src, string tgt)
    {
        var sn = DwgTranslator.Core.Models.TranslationLanguages.Name(src);
        var tn = DwgTranslator.Core.Models.TranslationLanguages.Name(tgt);
        return $"Translate the following CAD drawing text from {sn} to {tn}:\n\n---TEXT START---\n{text}\n---TEXT END---\n\nOutput ONLY the translated text. No explanations. Translate every source-language word. Keep the result as short as possible for a constrained CAD label. Use standard drawing abbreviations such as FB for feedback when unambiguous. Keep engineering symbols/units (KM, CB, M8, IP65, 24V, etc.) unchanged when they appear as tokens.";
    }

    private static string BuildCorrectionMessage(string text, string src, string tgt, string previous)
    {
        var target = tgt switch { "ZH" => "Chinese", "EN" => "English", "JA" => "Japanese", "KO" => "Korean", _ => tgt };
        return $"The previous answer was incomplete because it repeated source-language text. Translate ALL words below into {target}. Return one concise CAD label only; do not repeat any source-language characters.\n\nSOURCE:\n{text}\n\nPREVIOUS INVALID ANSWER:\n{previous}";
    }

    private static string TruncateForLog(string value) =>
        value.Length > 80 ? value[..80] + "..." : value;
}
