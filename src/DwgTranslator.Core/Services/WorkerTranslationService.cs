using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Adapts structured Worker batches to the entity pipeline. The caller supplies the existing
/// format-restoration routine; no model prompt or API key enters this adapter.
/// </summary>
public sealed class WorkerTranslationService : ITranslationService
{
    private readonly IApiClient _client;
    private readonly AppConfig _config;
    private readonly Func<string, string, string> _restoreFormat;
    private readonly Func<IEnumerable<GlossaryEntry>> _glossary;

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
    }

    public string CheckpointContext => System.Text.Json.JsonSerializer.Serialize(_glossary().ToList());

    public Task<List<TranslationPair>> TranslateBatchAsync(List<TextEntity> entities,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
        TranslateBatchWithProgressAsync(entities, sourceLanguage, targetLanguage, null, cancellationToken);

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var pairs = await TranslateBatchAsync(
            [new TextEntity { PlainText = text, RawText = text }], sourceLanguage, targetLanguage, cancellationToken)
            .ConfigureAwait(false);
        var pair = pairs.Single();
        if (pair.Status == TranslationStatus.TranslationFailed)
            throw new InvalidOperationException(pair.ErrorMessage);
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
        var terms = _glossary().Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Source)
                && !string.IsNullOrWhiteSpace(e.Target) && MatchesDirection(e.Direction, source, target))
            .Select(e => e.Clone()).GroupBy(e => e.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Where(e => e.PriorityWeight == g.Max(x => x.PriorityWeight))
                .ToList(), StringComparer.OrdinalIgnoreCase);
        var pending = new List<TextEntity>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entity.PlainText)
                || TranslationFilter.ShouldSkipTranslation(entity.PlainText, source, target))
                Complete(entity, entity.PlainText, TranslationStatus.Skipped, "", results, progress);
            else if (_config.GlossaryFirst && terms.TryGetValue(entity.PlainText.Trim(), out var matches))
            {
                // A tied, conflicting definition requires user resolution, not a model guess.
                if (matches.Select(e => e.Target.Trim()).Distinct(StringComparer.Ordinal).Count() != 1)
                    Complete(entity, "", TranslationStatus.TranslationFailed, "glossary_conflict", results, progress);
                else
                {
                    var translated = _restoreFormat(matches[0].Target.Trim(), entity.RawText);
                    var ok = !string.IsNullOrWhiteSpace(translated);
                    Complete(entity, translated, ok ? TranslationStatus.Translated : TranslationStatus.TranslationFailed,
                        ok ? "" : "empty_translation", results, progress, ok);
                }
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
                SourceLang = source, TargetLang = target,
                Protection = new ProtectionFlags
                {
                    ProtectDimensions = _config.ProtectDimensions,
                    ProtectTolerances = _config.ProtectTolerances,
                    ProtectModels = _config.ProtectModels,
                    GlossaryFirst = _config.GlossaryFirst
                },
                Glossary = terms.Values
                    .Where(group => group.Select(e => e.Target.Trim()).Distinct(StringComparer.Ordinal).Count() == 1)
                    .Select(group => group[0])
                    .Where(e => chunk.Any(item => item.PlainText.Contains(e.Source.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(e => e.PriorityWeight)
                    .Select(e => new GlossaryHint { Source = e.Source.Trim(), Target = e.Target.Trim() }).ToList(),
                Items = chunk.Select((e, id) => new TranslationItem
                {
                    Id = id, Text = e.PlainText, Height = e.Height,
                    Rotation = e.Rotation * 180.0 / Math.PI, // TextEntity has no layer field yet.
                    Context = e.BlockName
                }).ToList()
            };
            var response = await _client.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            for (int retry = 0; retry < 3 && !response.Success &&
                response.ErrorCode is "network_error" or "upstream_unavailable" or "request_in_progress"; retry++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << retry), cancellationToken).ConfigureAwait(false);
                response = await _client.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.Success)
                throw new InvalidOperationException(response.ErrorCode ?? response.Message ?? "worker_batch_failed");

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
                else if (!TranslationQualityValidator.IsAcceptable(entity.PlainText,
                    matches[0].TranslatedText ?? "", source, target)) error = "invalid_translation";
                else
                {
                    translated = _restoreFormat(matches[0].TranslatedText!, entity.RawText);
                    if (string.IsNullOrWhiteSpace(translated)) error = "empty_translation";
                }
                Complete(entity, translated, error.Length == 0 ? TranslationStatus.Translated
                    : TranslationStatus.TranslationFailed, error, results, progress);
            }
        }
        return results;
    }

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

