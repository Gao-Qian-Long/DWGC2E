using System.Text.RegularExpressions;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Translation;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements deduplicated concurrent translation pipeline.
/// Same PlainText is only translated once; results are mapped back to all entities with same text.
/// Concurrency scales adaptively based on entity count.
/// </summary>
public class TranslationService : ITranslationService
{
    private readonly IGlossaryService _glossaryService;
    private readonly FormatCodeParser _formatCodeParser;
    private readonly IDeepSeekClient _deepSeekClient;
    private readonly string _systemPrompt;
    private readonly int _batchSize;
    private readonly int _maxRetryCount;
    private readonly TranslationConsistencyService _consistencyService;

    public TranslationService(
        IGlossaryService glossaryService,
        FormatCodeParser formatCodeParser,
        IDeepSeekClient deepSeekClient,
        string systemPrompt,
        int batchSize = 50,
        int maxRetryCount = 3,
        TranslationConsistencyService? consistencyService = null,
        int maxConcurrency = 5)
    {
        _glossaryService = glossaryService;
        _formatCodeParser = formatCodeParser;
        _deepSeekClient = deepSeekClient;
        _systemPrompt = systemPrompt;
        _batchSize = batchSize;
        _maxRetryCount = maxRetryCount;
        _consistencyService = consistencyService ?? new TranslationConsistencyService();
    }

    /// <inheritdoc/>
    public async Task<List<TranslationPair>> TranslateBatchAsync(
        List<TextEntity> entities, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return await TranslateBatchWithProgressAsync(entities, sourceLanguage, targetLanguage, null, cancellationToken);
    }

    /// <summary>
    /// Deduplicated concurrent translation with streaming progress.
    /// Groups entities by PlainText, translates each unique text once, 
    /// then maps results to all entities with the same PlainText.
    /// </summary>
    public async Task<List<TranslationPair>> TranslateBatchWithProgressAsync(
        List<TextEntity> entities, string sourceLanguage, string targetLanguage,
        IProgress<TranslationPair>? progress, CancellationToken cancellationToken = default)
    {
        var allResults = new List<TranslationPair>();

        // Step 1: Group by unique PlainText to avoid duplicate API calls
        var groups = entities
            .Where(e => !string.IsNullOrWhiteSpace(e.PlainText))
            .GroupBy(e => e.PlainText.Trim(), StringComparer.Ordinal)
            .ToList();

        int uniqueCount = groups.Count;
        int totalCount = entities.Count;
        int savedCalls = totalCount - uniqueCount;

        Log.Information("Translation: {Unique} unique texts from {Total} entities (saved {Saved} API calls)",
            uniqueCount, totalCount, savedCalls);

        // Step 2: Adaptive concurrency based on count
        int concurrency = Math.Clamp(uniqueCount, 5, 30);
        Log.Information("Using {Concurrency} concurrent streams for {Count} unique texts", concurrency, uniqueCount);

        var semaphore = new SemaphoreSlim(concurrency, concurrency);
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

                    lock (mapLock)
                    {
                        translationMap[plainText] = pair;
                    }

                    // Report progress and emit results for ALL entities with same text
                    int localDone = Interlocked.Increment(ref completed);
                    foreach (var entity in group)
                    {
                        var result = new TranslationPair
                        {
                            Handle = entity.Handle,
                            SourceText = entity.PlainText,
                            TranslatedText = pair.TranslatedText,
                            GlossaryHit = pair.GlossaryHit,
                            Status = pair.Status
                        };

                        lock (mapLock) allResults.Add(result);
                        progress?.Report(result);
                    }

                    if (localDone % 10 == 0 || localDone == uniqueCount)
                    {
                        Log.Debug("Translation progress: {Done}/{Total} unique texts", localDone, uniqueCount);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        _consistencyService.FlushCache();

        Log.Information("Translation complete: {Unique} unique -> {Total} total results", uniqueCount, allResults.Count);
        return allResults;
    }

    /// <inheritdoc/>
    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var (translated, _) = await TranslateWithMetadataAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        return translated;
    }

    private static readonly Regex NumericOnlyRegex = new(
        @"^[\s\d\.\,\+\-\*\/\=<>≤≥±°\#\%‰〇零一二三四五六七八九十百千万亿φΦ⌀ⓧⓓ]+$",
        RegexOptions.Compiled);

    private async Task<TranslationPair> TranslateSingleAsync(
        TextEntity entity, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entity.PlainText))
                return new TranslationPair { Handle = entity.Handle, Status = TranslationStatus.Skipped };

            // Skip pure numeric/dimension values (no actual text to translate)
            if (NumericOnlyRegex.IsMatch(entity.PlainText.Trim()))
            {
                Log.Debug("Skipped numeric-only text: {Text}", entity.PlainText);
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = entity.PlainText, GlossaryHit = false,
                    Status = TranslationStatus.Skipped
                };
            }

            // Check cache
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
        return (CleanTranslationOutput(translated), glossaryHit);
    }

    private string RestoreFormatCodes(string translated, string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return translated;
        var codes = _formatCodeParser.ExtractFormatCodes(rawText);
        if (codes.Count == 0) return translated;
        var (_, _, oc) = _formatCodeParser.Parse(rawText);
        return oc.Count > 0 ? _formatCodeParser.Restore(translated, oc) : _formatCodeParser.Restore(translated, codes);
    }

    private static string CleanTranslationOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = text.Trim();
        if (text.StartsWith("Translated", StringComparison.OrdinalIgnoreCase) && text.Contains(":"))
        {
            var ci = text.IndexOf(':');
            if (ci > 0 && ci < 30) text = text[(ci + 1)..].Trim();
        }
        return text;
    }

    private async Task<string> TranslateWithRetryAsync(
        string text, string src, string tgt, CancellationToken ct)
    {
        Exception? last = null;
        var msg = BuildUserMessage(text, src, tgt);
        for (int i = 0; i <= _maxRetryCount; i++)
        {
            try
            {
                if (i > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)), ct);
                var result = await _deepSeekClient.ChatCompletionAsync(_systemPrompt, msg, ct);
                if (!string.IsNullOrWhiteSpace(result)) return CleanTranslationOutput(result);
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