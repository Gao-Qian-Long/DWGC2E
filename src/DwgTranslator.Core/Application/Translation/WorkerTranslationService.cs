using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Adapts structured Worker batches to the entity pipeline. The caller supplies the existing
/// format-restoration routine; no model prompt or API key enters this adapter.
/// </summary>
public sealed class WorkerTranslationService : ITranslationService, IWritebackGlossaryProvider, ITranslationCheckpointContextProvider, IAsyncTranslationCheckpointContextProvider
{
    public IReadOnlyList<GlossaryEntry> GetWritebackGlossary(string sourceLanguage, string targetLanguage) =>
        EffectiveGlossary.Resolve(_glossary(), sourceLanguage, targetLanguage);

    private readonly IApiClient _client;
    private readonly AppConfig _config;
    private readonly Func<string, string, string> _restoreFormat;
    private readonly Func<IEnumerable<GlossaryEntry>> _glossary;
    private readonly ITranslationContextClient? _translationContextClient;
    private string _contextVersion = "worker-context-unresolved-v1";

    public WorkerTranslationService(IApiClient client, AppConfig config,
        Func<string, string, string> restoreFormat,
        Func<IEnumerable<GlossaryEntry>>? glossary = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (!string.Equals(client.ModeName, "worker", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Worker adapter requires a Worker client.", nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _restoreFormat = restoreFormat ?? throw new ArgumentNullException(nameof(restoreFormat));
        _glossary = glossary ?? (() => Array.Empty<GlossaryEntry>());
        _translationContextClient = client as ITranslationContextClient;
        if (_translationContextClient != null)
            _contextVersion = _translationContextClient.CachedTranslationContextVersion;
    }

    public async Task PrepareCheckpointContextAsync(string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (_translationContextClient == null) return;
        var refreshed = await _translationContextClient.RefreshTranslationContextVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(refreshed)) _contextVersion = refreshed!;
    }

    public string GetCheckpointContext(string sourceLanguage, string targetLanguage)
    {
        var source = TranslationLanguages.Normalize(sourceLanguage);
        var target = TranslationLanguages.Normalize(targetLanguage);
        var terms = EffectiveGlossary.Resolve(_glossary(), source, target)
            .OrderBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Target, StringComparer.Ordinal)
            .ThenBy(entry => entry.PriorityWeight)
            .Select(entry => new
            {
                entry.Source,
                entry.Target,
                entry.SourceLang,
                entry.TargetLang,
                entry.PriorityWeight
            });
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            Schema = 2,
            Service = "worker",
            ContextVersion = _contextVersion,
            Terms = terms
        });
    }

    public Task<List<TranslationPair>> TranslateBatchAsync(List<TextEntity> entities,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
        TranslateBatchWithProgressAsync(entities, sourceLanguage, targetLanguage, null, cancellationToken);

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("worker_empty_input: 待翻译文本为空。", nameof(text));
        var pairs = await TranslateBatchAsync(
            [new TextEntity { PlainText = text, RawText = text }], sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);
        if (pairs.Count == 0)
            throw new InvalidOperationException("worker_empty_result: 翻译没有返回任何结果。");
        var pair = pairs[0];
        if (pair.Status == TranslationStatus.TranslationFailed)
            throw new InvalidOperationException(string.IsNullOrEmpty(pair.ErrorMessage) ? "worker_translation_failed" : pair.ErrorMessage);
        return pair.TranslatedText;
    }

    public async Task<List<TranslationPair>> TranslateBatchWithProgressAsync(List<TextEntity> entities,
        string sourceLanguage, string targetLanguage, IProgress<TranslationPair>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        cancellationToken.ThrowIfCancellationRequested();
        var source = TranslationLanguages.Normalize(sourceLanguage);
        var target = TranslationLanguages.Normalize(targetLanguage);
        var results = new List<TranslationPair>(entities.Count);
        // Snapshot once so edits to the glossary cannot change later batches mid-task.
        var (resolvedTerms, conflictingTerms) = EffectiveGlossary.ResolveOrCaptureConflicts(_glossary(), source, target);
        // 大小写不敏感的键比较：术语匹配本来就是 OrdinalIgnoreCase，"Valve"/"VALVE" 必须命中同一条术语。
        var terms = resolvedTerms.ToDictionary(e => e.Source, e => new List<GlossaryEntry> { e }, StringComparer.OrdinalIgnoreCase);
        const string GlossaryConflict = "glossary_conflict";
        var pending = new List<TextEntity>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entity.PlainText)
                || (EffectiveGlossary.Match(entity.PlainText, resolvedTerms).Count == 0
                    && TranslationFilter.ShouldSkipTranslation(entity.PlainText, source, target)))
                Complete(entity, entity.PlainText, TranslationStatus.Skipped, "", results, progress);
            else if (EffectiveGlossary.HasConflictingHit(entity.PlainText, conflictingTerms))
                // 命中冲突术语的条目必须由用户裁决，不能整批中止，也不能让模型猜。
                Complete(entity, "", TranslationStatus.TranslationFailed, GlossaryConflict, results, progress);
            else if (terms.TryGetValue(entity.PlainText.Trim(), out var matches)
                && EffectiveGlossary.Match(entity.PlainText, resolvedTerms) is { Count: 1 } selected
                && selected[0].SourceTerm.Length == entity.PlainText.Trim().Length)
            {
                var translated = _restoreFormat(matches[0].Target.Trim(), entity.RawText);
                var ok = !string.IsNullOrWhiteSpace(translated);
                Complete(entity, translated, ok ? TranslationStatus.Translated : TranslationStatus.TranslationFailed,
                    ok ? "" : "empty_translation", results, progress, ok);
            }
            else pending.Add(entity);
        }

        // Bounded batches. Transport retries reuse the request ID to avoid duplicate billing.
        var size = Math.Clamp(_config.BatchSize, 1, 100);
        for (int offset = 0; offset < pending.Count; offset += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = pending.Skip(offset).Take(size).ToList();
            var request = new TranslationBatchRequest
            {
                BillingMode = TranslationBillingContext.Current?.Mode, BillingTaskId = TranslationBillingContext.Current?.TaskId,
                SourceLang = source, TargetLang = target,
                Protection = new ProtectionFlags
                {
                    ProtectDimensions = _config.ProtectDimensions,
                    ProtectTolerances = _config.ProtectTolerances,
                    ProtectModels = _config.ProtectModels,
                    GlossaryFirst = true
                },
                Glossary = resolvedTerms
                    .Where(e => chunk.Any(item => item.PlainText.Contains(e.Source.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(e => e.PriorityWeight)
                    .Select(e => new GlossaryHint { Source = e.Source.Trim(), Target = e.Target.Trim(), Priority = e.PriorityWeight }).ToList(),
                Items = chunk.Select((e, id) => new TranslationItem
                {
                    Id = id, Text = e.PlainText, Height = e.Height,
                    Rotation = e.Rotation * 180.0 / Math.PI, // TextEntity has no layer field yet.
                    Context = e.BlockName
                }).ToList()
            };
            var response = await SendWithTransientRetryAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.Success)
            {
                // 限流/超时的重试预算用尽后，只把这一批标记失败：同图的其它分块不受影响。
                // 额度、鉴权、请求非法这类确定性问题仍然立刻上抛，不拿模型重试掩盖真实原因。
                if (IsTransient(response.ErrorCode))
                {
                    var reason = response.ErrorCode ?? "worker_batch_failed";
                    foreach (var item in chunk.Select((e, id) => (Entity: e, Id: id)))
                        Complete(item.Entity, "", TranslationStatus.TranslationFailed, reason, results, progress);
                    continue;
                }
                throw new InvalidOperationException(response.ErrorCode ?? response.Message ?? "worker_batch_failed");
            }

            // Only requested IDs participate; duplicates are ambiguous, not a silent first-result win.
            var byId = response.Items.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.ToList());
            for (int id = 0; id < chunk.Count; id++)
            {
                var entity = chunk[id];
                string error = "";
                string translated = "";
                if (!byId.TryGetValue(id, out var matches)) error = "missing_result";
                else if (matches.Count != 1) error = "duplicate_result";
                else if (!string.IsNullOrEmpty(matches[0].ErrorCode)) error = matches[0].ErrorCode!;
                else if (EffectiveGlossary.Match(entity.PlainText, resolvedTerms)
                    .Any(m => !(matches[0].TranslatedText ?? "").Contains(m.TargetTerm, StringComparison.Ordinal)))
                    error = "glossary_not_preserved";
                else if (!EffectiveGlossary.IsAcceptableTranslation(entity.PlainText, matches[0].TranslatedText ?? "",
                    source, target, resolvedTerms)) error = "invalid_translation";
                else
                {
                    translated = _restoreFormat(matches[0].TranslatedText!, entity.RawText);
                    if (string.IsNullOrWhiteSpace(translated)) error = "empty_translation";
                }
                Complete(entity, translated, error.Length == 0 ? TranslationStatus.Translated
                    : TranslationStatus.TranslationFailed, error, results, progress,
                    error.Length == 0 && EffectiveGlossary.Match(entity.PlainText, resolvedTerms).Count > 0);
            }
        }
        return results;
    }

    /// <summary>
    /// 发送一次分块请求，并对可恢复的失败重试：网络错误、上游不可用、重复请求、限流（429）。
    /// 429 之前完全不在重试列表里——服务端明确要求稍后重试，客户端却立刻把整张图纸判失败；
    /// 反过来"额度不足"这类终态错误反而被重试了三次。
    /// 重试总等待有上限，超限就交给调用方把这一批标记失败，不让单张图纸无限期挂住。
    /// </summary>
    private async Task<TranslationBatchResult> SendWithTransientRetryAsync(
        TranslationBatchRequest request, CancellationToken cancellationToken)
    {
        var response = await _client.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        var waited = TimeSpan.Zero;
        for (var attempt = 0; attempt < MaxTransientRetries && !response.Success && IsTransient(response.ErrorCode); attempt++)
        {
            var delay = RetryDelay(response, attempt);
            if (waited + delay > RetryBudget) break;
            waited += delay;
            Log.Warning("翻译分块请求失败（{ErrorCode}），{Delay} 秒后重试（{Attempt}/{Max}）",
                response.ErrorCode, delay.TotalSeconds, attempt + 1, MaxTransientRetries);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            response = await _client.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    /// <summary>限流的 Retry-After 优先于本地退避；两种都夹在单次等待上限内。</summary>
    private static TimeSpan RetryDelay(TranslationBatchResult response, int attempt)
    {
        var requested = response.RetryAfterSeconds is { } seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(1 << Math.Min(attempt, 4));
        return requested > MaxSingleRetryDelay ? MaxSingleRetryDelay : requested;
    }

    /// <summary>可恢复的失败：重试有意义；确定性问题（额度、鉴权、请求非法）不在其中。</summary>
    private static bool IsTransient(string? errorCode) =>
        errorCode is "network_error" or "upstream_unavailable" or "request_in_progress" or "rate_limited";

    /// <summary>重试次数上限；配合 <see cref="RetryBudget"/> 保证单张图纸不会无限期挂住。</summary>
    private const int MaxTransientRetries = 4;

    /// <summary>单个分块请求允许等待的总重试时长。</summary>
    private static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(20);

    /// <summary>单次重试等待上限（服务端 Retry-After 再长也夹到这里）。</summary>
    private static readonly TimeSpan MaxSingleRetryDelay = TimeSpan.FromSeconds(8);

    private static bool MatchesDirection(string? direction, string source, string target)
    {
        if (string.IsNullOrWhiteSpace(direction)) return true;
        // Try every delimiter: language codes themselves may contain '-' (ZH-TW, EN-US).
        var value = direction.Trim();
        for (var i = 1; i < value.Length - 1; i++)
            if (value[i] == '-' && TranslationLanguages.Normalize(value[..i]) == source
                && TranslationLanguages.Normalize(value[(i + 1)..]) == target) return true;
        return false;
    }

    private static void Complete(TextEntity entity, string text, TranslationStatus status, string error,
        List<TranslationPair> results, IProgress<TranslationPair>? progress, bool glossaryHit = false)
    {
        entity.TranslatedText = text;
        entity.Status = status;
        entity.GlossaryHit = glossaryHit; // Worker cache hits do not imply glossary matches.
        var pair = new TranslationPair
        {
            Handle = entity.Handle, SourceFilePath = entity.SourceFilePath,
            SourceText = entity.PlainText, TranslatedText = text, Status = status, ErrorMessage = error, GlossaryHit = glossaryHit
        };
        results.Add(pair);
        progress?.Report(pair);
    }
}
