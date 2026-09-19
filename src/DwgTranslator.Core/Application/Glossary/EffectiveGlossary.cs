using DwgTranslator.Core.Models;
namespace DwgTranslator.Core.Services;

/// <summary>Shared, immutable direction/priority resolution for UI and both translation paths.</summary>
public static class EffectiveGlossary
{
    public const string DefaultCategory = "默认分类";
    public static void Normalize(GlossaryEntry e)
    {
        e.Source = (e.Source ?? "").Trim(); e.Target = (e.Target ?? "").Trim();
        e.SourceLang = string.IsNullOrWhiteSpace(e.SourceLang) ? "" : TranslationLanguages.Normalize(e.SourceLang);
        e.TargetLang = string.IsNullOrWhiteSpace(e.TargetLang) ? "" : TranslationLanguages.Normalize(e.TargetLang);
        e.Category = string.IsNullOrWhiteSpace(e.Category) ? DefaultCategory : e.Category.Trim();
        if (!Guid.TryParse(e.LocalId, out _)) e.LocalId = Guid.NewGuid().ToString("D");
    }
    public static bool Valid(GlossaryEntry e) => !string.IsNullOrWhiteSpace(e.Source) && e.Source.Length <= 500 && !string.IsNullOrWhiteSpace(e.Target) && e.Target.Length <= 500 && (e.Category?.Length ?? 0) <= 128 && (e.Folder?.Length ?? 0) <= 128 && (e.CloudNote?.Length ?? 0) <= 1000;
    public static IReadOnlyList<GlossaryEntry> Conflicts(IEnumerable<GlossaryEntry> entries) => entries
        .Where(e => e.Enabled && !e.DirectionPending && Valid(e))
        .GroupBy(e => (e.SourceLang, e.TargetLang, Source: e.Source.Trim().ToUpperInvariant()))
        .Select(g => g.Where(e => e.PriorityWeight == g.Max(x => x.PriorityWeight)).ToList())
        .Where(g => g.Select(e => e.Target.Trim()).Distinct(StringComparer.Ordinal).Count() > 1).SelectMany(g => g).ToList();

    /// <summary>
    /// 解析出术语表，并把"无法自动裁决"的冲突单独摘出来，而不是整批抛异常。
    /// <para>
    /// 旧实现在 <see cref="Resolve"/> 里对任何冲突直接抛；调用方（图纸批处理、单条翻译）只能整张图纸失败，
    /// 逐条降级的分支永远走不到。冲突是"某一条术语没法自动裁决"，不是"这份图纸不能翻"：把冲突条目交回给
    /// 调用方逐条标记，其余条目照常翻译。
    /// </para>
    /// </summary>
    public static (IReadOnlyList<GlossaryEntry> Terms, IReadOnlyList<GlossaryEntry> Conflicting) ResolveOrCaptureConflicts(
        IEnumerable<GlossaryEntry> entries, string source, string target)
    {
        var normalized = entries.Select(e => e.Clone()).ToList();
        foreach (var e in normalized) Normalize(e);
        var directionTerms = normalized
            .Where(e => e.Enabled && !e.DirectionPending && Valid(e)
                && e.SourceLang == TranslationLanguages.Normalize(source)
                && e.TargetLang == TranslationLanguages.Normalize(target))
            .Select(e => e.Clone()).ToList();
        var conflicts = Conflicts(directionTerms);
        var conflictingIds = conflicts.Select(e => e.LocalId).ToHashSet(StringComparer.Ordinal);
        var resolved = directionTerms
            .Where(e => !conflictingIds.Contains(e.LocalId))
            .GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.PriorityWeight).First())
            .OrderByDescending(e => e.PriorityWeight).ThenByDescending(e => e.Source.Length).ToList();
        return (resolved, conflicts);
    }

    /// <summary>命中的术语里是否有冲突条目（命中冲突就不能猜译文，只能要求用户先裁决）。</summary>
    public static bool HasConflictingHit(string text, IReadOnlyList<GlossaryEntry> conflicting) =>
        conflicting.Count > 0 && Match(text, conflicting).Count > 0;

    /// <summary>
    /// 术语表指纹：把"会影响译文的一切"压成一段稳定字符串。缓存键接上它之后，用户改了术语、
    /// 启停术语、换账号换了术语表，旧译文都不再被当成同一个键命中——避免静默返回过期译文。
    /// </summary>
    public static string Fingerprint(IReadOnlyList<GlossaryEntry> resolved)
    {
        var canonical = resolved
            .Select(e => new[] { e.Source, e.Target, e.SourceLang, e.TargetLang, e.PriorityWeight.ToString(System.Globalization.CultureInfo.InvariantCulture), e.SourceKind.ToString() })
            .OrderBy(fields => fields[0], StringComparer.Ordinal)
            .ThenBy(fields => fields[1], StringComparer.Ordinal)
            .ThenBy(fields => fields[2], StringComparer.Ordinal)
            .ThenBy(fields => fields[3], StringComparer.Ordinal)
            .ThenBy(fields => fields[4], StringComparer.Ordinal)
            .ThenBy(fields => fields[5], StringComparer.Ordinal)
            .ToList();
        var payload = System.Text.Json.JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload)))[..16];
    }

    /// <summary>缓存命名空间：语言方向 + 术语表指纹（<see cref="TranslationConsistencyService"/> 的键前缀）。</summary>
    public static string ScopedDirection(string sourceLanguage, string targetLanguage, string glossaryFingerprint) =>
        TranslationLanguages.Normalize(sourceLanguage) + ">" + TranslationLanguages.Normalize(targetLanguage)
        + "#g" + glossaryFingerprint;

    public static IReadOnlyList<GlossaryEntry> Resolve(IEnumerable<GlossaryEntry> entries, string source, string target)
    {
        var (terms, conflicts) = ResolveOrCaptureConflicts(entries, source, target);
        if (conflicts.Count > 0) throw new InvalidOperationException("当前语言方向存在术语冲突，请先在术语库中解决后再翻译。");
        return terms;
    }
    /// <summary>
    /// Safely applies resolved terminology to an existing reviewed translation. Exact source terms may
    /// replace the whole translation; terms embedded in a sentence are changed only when the untranslated
    /// source token is still present in the translation. This avoids guessing where an already translated
    /// phrase belongs.
    /// </summary>
    public static string ApplyToExistingTranslation(string original, string translated, IReadOnlyList<GlossaryEntry> resolvedTerms, out int sourceHitCount)
    {
        original ??= string.Empty;
        translated ??= string.Empty;
        sourceHitCount = 0;
        var exact = resolvedTerms.FirstOrDefault(term =>
            !string.IsNullOrWhiteSpace(term.Source) && string.Equals(original.Trim(), term.Source.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            sourceHitCount = 1;
            return exact.Target;
        }

        var revised = translated;
        foreach (var term in resolvedTerms.Where(term => !string.IsNullOrWhiteSpace(term.Source) &&
                     original.Contains(term.Source, StringComparison.OrdinalIgnoreCase)))
        {
            sourceHitCount++;
            if (revised.Contains(term.Source, StringComparison.OrdinalIgnoreCase))
                revised = revised.Replace(term.Source, term.Target, StringComparison.OrdinalIgnoreCase);
        }
        return revised;
    }
    public static List<GlossaryMatch> Match(string text, IReadOnlyList<GlossaryEntry> entries)
    {
        var result = new List<GlossaryMatch>(); var occupied = new bool[text.Length];
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Source)) continue;
            for (int start = 0, pos; start < text.Length && (pos = text.IndexOf(e.Source, start, StringComparison.OrdinalIgnoreCase)) >= 0; start = pos + e.Source.Length)
            {
                if (Enumerable.Range(pos, e.Source.Length).Any(i => occupied[i])) continue;
                result.Add(new GlossaryMatch { SourceTerm = text.Substring(pos, e.Source.Length), TargetTerm = e.Target, Position = pos, Placeholder = $"__GLOSSARY_{result.Count}__" });
                for (int i = pos; i < pos + e.Source.Length; i++) occupied[i] = true;
            }
        }
        return result;
    }
    /// <summary>Validate AI-owned text without rejecting the user's exact prescribed wording.</summary>
    public static bool IsAcceptableTranslation(string source, string translated, string sourceLanguage, string targetLanguage, IReadOnlyList<GlossaryEntry> entries)
    {
        var matches=Match(source,entries);
        if(matches.Count==0)return TranslationQualityValidator.IsAcceptable(source,translated,sourceLanguage,targetLanguage);
        foreach(var m in matches.OrderByDescending(m=>m.Position))
            source=source.Remove(m.Position,m.SourceTerm.Length).Insert(m.Position," ");
        foreach(var m in matches.OrderByDescending(m=>m.TargetTerm.Length))
        {
            var position=translated.IndexOf(m.TargetTerm,StringComparison.Ordinal);
            if(position<0)return false;
            translated=translated.Remove(position,m.TargetTerm.Length).Insert(position," ");
        }
        if(!source.Any(char.IsLetter))return true;
        return TranslationQualityValidator.IsAcceptable(source,translated,sourceLanguage,targetLanguage);
    }
}
