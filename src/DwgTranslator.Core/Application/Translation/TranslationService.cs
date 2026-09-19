using DwgTranslator.Core.Models;
using DwgTranslator.Core.Translation;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements deduplicated concurrent translation pipeline.
/// Same PlainText is only translated once; results are mapped back to all entities with same text.
/// </summary>
public class TranslationService : ITranslationService, IWritebackGlossaryProvider
{
    public IReadOnlyList<GlossaryEntry> GetWritebackGlossary(string sourceLanguage, string targetLanguage) =>
        EffectiveGlossary.Resolve(_glossaryService.GetAllEntries(), sourceLanguage, targetLanguage);

    private readonly IGlossaryService _glossaryService;
    private readonly IFormatCodeParser _formatCodeParser;
    private readonly IFormatCodeRestorer _formatCodeRestorer;
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
        _formatCodeRestorer = new FormatCodeRestorer(formatCodeParser);
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
        var (glossarySnapshot, glossaryConflicts) = ResolveGlossary(sourceLanguage, targetLanguage);

        // Keep the direction local to this batch. A mutable direction on the shared cache races
        // when callers translate different language pairs concurrently. The glossary fingerprint is
        // part of the scope: a glossary edit must not let a pre-edit translation be reused.
        var cacheDirection = EffectiveGlossary.ScopedDirection(
            sourceLanguage, targetLanguage, EffectiveGlossary.Fingerprint(glossarySnapshot));

        // 大小写变体（"Valve"/"VALVE"）在 CAD 图上指同一条标签，术语匹配也是大小写不敏感的；
        // 分组按 OrdinalIgnoreCase，避免同一条文字被翻两遍、两个实体拿到不同译文。
        var groups = entities
            .Where(e => !string.IsNullOrWhiteSpace(e.PlainText))
            .GroupBy(e => e.PlainText.Trim(), StringComparer.OrdinalIgnoreCase)
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
            // 同一组里可能有不同大小写的写法，统一取第一条作为规范文本：分组键在
            // OrdinalIgnoreCase 下是小写化的，不能直接当作要翻译的原文。
            var plainText = group.First().PlainText.Trim();
            var representative = group.First();

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var pair = await TranslateSingleAsync(representative, sourceLanguage, targetLanguage, cacheDirection, cancellationToken, glossarySnapshot, glossaryConflicts);

                    lock (mapLock) { translationMap[plainText] = pair; }

                    int localDone = Interlocked.Increment(ref completed);
                    foreach (var entity in group)
                    {
                        // Deduplication reuses only the plain translated content. Each entity
                        // must restore its own RawText format template independently.
                        var entityTranslation = pair.Status == TranslationStatus.Translated
                            ? RestoreFormatCodes(!pair.GlossaryHit && targetLanguage == "EN"
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
        // 单条翻译直接进模型路径，没有批次级的术语快照。这里自己解析一次：既让术语生效，
        // 也让方向级冲突有明确文案，而不是抛一句调用方看不懂的异常。
        var (snapshot, conflicts) = ResolveGlossary(sourceLanguage, targetLanguage);
        if (EffectiveGlossary.HasConflictingHit(text, conflicts))
            throw new InvalidOperationException("glossary_conflict: 该文字命中了冲突术语，请先在术语库中解决后再翻译。");
        var (translated, _) = await TranslateWithMetadataAsync(text, sourceLanguage, targetLanguage, cancellationToken, snapshot);
        return translated;
    }

    private async Task<TranslationPair> TranslateSingleAsync(
        TextEntity entity, string sourceLanguage, string targetLanguage, string cacheDirection, CancellationToken ct,
        IReadOnlyList<GlossaryEntry> glossarySnapshot, IReadOnlyList<GlossaryEntry> glossaryConflicts)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entity.PlainText))
                return new TranslationPair { Handle = entity.Handle, Status = TranslationStatus.Skipped };

            // 命中冲突术语的条目只能由用户裁决，不能让模型猜：整批抛异常会把这份图纸里
            // 其余能翻的文字一起拖下水，所以在这里逐条标失败，其余条目照常翻译。
            if (EffectiveGlossary.HasConflictingHit(entity.PlainText, glossaryConflicts))
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText, TranslatedText = string.Empty,
                    GlossaryHit = false, Status = TranslationStatus.TranslationFailed,
                    ErrorMessage = "glossary_conflict"
                };

            var authoritativeTerms = EffectiveGlossary.Match(entity.PlainText, glossarySnapshot);
            if (authoritativeTerms.Count == 0 && TranslationFilter.IsNumericOnly(entity.PlainText))
            {
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = entity.PlainText, GlossaryHit = false, Status = TranslationStatus.Skipped
                };
            }

            if (authoritativeTerms.Count == 0 && TranslationFilter.ShouldSkipTranslation(entity.PlainText, sourceLanguage, targetLanguage))
            {
                return new TranslationPair
                {
                    Handle = entity.Handle, SourceText = entity.PlainText,
                    TranslatedText = entity.PlainText, GlossaryHit = false, Status = TranslationStatus.Skipped
                };
            }

            if (authoritativeTerms.Count == 0 && _consistencyService.TryGetMatch(entity.PlainText, cacheDirection, out var cached))
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
                _consistencyService.RemoveFromCache(entity.PlainText, cacheDirection);
            }

            var (translated, glossaryHit) = await TranslateWithMetadataAsync(
                entity.PlainText, sourceLanguage, targetLanguage, ct, glossarySnapshot);

            if (!glossaryHit && !TranslationQualityValidator.IsAcceptable(
                    entity.PlainText, translated, sourceLanguage, targetLanguage))
                throw new InvalidDataException("Translation response still contains source-language text");

            if (!glossaryHit && !string.IsNullOrEmpty(translated))
                _consistencyService.AddToCache(entity.PlainText, translated, cacheDirection);

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
        string plainText, string sourceLanguage, string targetLanguage, CancellationToken ct, IReadOnlyList<GlossaryEntry>? glossarySnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return (string.Empty, false);

        var matches = EffectiveGlossary.Match(plainText, glossarySnapshot ?? ResolveGlossary(sourceLanguage, targetLanguage).Terms);
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
        {
            // 整条文字由术语覆盖时，正确答案就是术语的目标值。旧实现在这里返回 residual——
            // 而 residual 是"把术语占位符还原成源术语"的文本，于是整条术语命中反而返回源文，
            // 下游质量校验（检测到源语言残留）把这条判成翻译失败。
            var glossaryTarget = TranslationFilter.CleanTranslationOutput(RestorePlaceholdersToTargets(residual, matches));
            if (!string.IsNullOrWhiteSpace(glossaryTarget)) return (glossaryTarget, true);
        }

        var translated = await TranslateWithRetryAsync(cleanText, sourceLanguage, targetLanguage, ct);
        if (matches.Any(m => translated.Split(m.Placeholder, StringSplitOptions.None).Length != 2))
            throw new InvalidDataException("AI 未完整保留已启用术语，请重试该条翻译。");
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

    /// <summary>
    /// 把术语占位符还原成术语的<b>目标</b>值（<see cref="IGlossaryService.RestorePlaceholders"/> 还原的是源术语）。
    /// 整条术语覆盖时用它产出最终译文，不要再让模型回答一遍。
    /// </summary>
    private static string RestorePlaceholdersToTargets(string text, List<GlossaryMatch> matches)
    {
        var result = text;
        foreach (var match in matches)
            result = result.Replace(match.Placeholder, match.TargetTerm, StringComparison.Ordinal);
        return result;
    }

    /// <summary>
    /// 解析本次翻译要用的术语表，并单独摘出无法自动裁决的冲突条目。
    /// 冲突不再让整张图纸失败：调用方对命中冲突的条目逐条标失败，其余条目照常翻译。
    /// </summary>
    private (IReadOnlyList<GlossaryEntry> Terms, IReadOnlyList<GlossaryEntry> Conflicting) ResolveGlossary(
        string sourceLanguage, string targetLanguage)
    {
        var (terms, conflicts) = EffectiveGlossary.ResolveOrCaptureConflicts(
            _glossaryService.GetAllEntries(), sourceLanguage, targetLanguage);
        if (conflicts.Count > 0)
            Log.Warning("术语表存在 {Count} 条冲突条目，命中这些条目的文字将逐条标记失败：{Terms}",
                conflicts.Count, string.Join(",", conflicts.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase)));
        return (terms, conflicts);
    }

    public string RestoreFormatCodes(string translated, string rawText) =>
        _formatCodeRestorer.Restore(translated, rawText);

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
