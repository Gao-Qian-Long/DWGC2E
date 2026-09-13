using System;
using System.Collections.Generic;
using System.Linq;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// 一个术语冲突：同一个原文在不同来源下给出了不同译法。
/// 界面（术语库页「冲突术语处理」卡）直接展示候选 A/B 与建议。
/// </summary>
public sealed class GlossaryConflict
{
    public string Source { get; set; } = string.Empty;

    /// <summary>优先级更高的候选。</summary>
    public GlossaryEntry CandidateA { get; set; } = new();

    /// <summary>优先级较低的候选。</summary>
    public GlossaryEntry CandidateB { get; set; } = new();

    /// <summary>建议文案：使用用户术语 / 使用企业术语 / 使用系统术语 / 请确认。</summary>
    public string Suggestion { get; set; } = string.Empty;

    /// <summary>true = 同优先级冲突，必须由用户确认，不能自动裁决。</summary>
    public bool NeedsConfirmation { get; set; }

    public override string ToString() => $"{Source}: {CandidateA.Target} vs {CandidateB.Target} → {Suggestion}";
}

/// <summary>
/// 术语冲突检测与优先级裁决（提示词 §8）。
///
/// 优先级链：用户术语 &gt; 企业术语 &gt; 系统术语 &gt; AI 默认。
/// 关键约束：**冲突不能静默覆盖**——同优先级时返回 NeedsConfirmation，由界面提示用户选择。
/// 本类为纯逻辑，便于单元测试；不读写文件。
/// </summary>
public static class GlossaryConflictDetector
{
    /// <summary>按优先级裁决单个原文应使用的术语；无候选时返回 null。</summary>
    public static GlossaryEntry? Resolve(string source, IEnumerable<GlossaryEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(source) || entries == null) return null;

        return entries
            .Where(e => e.Enabled && SameTerm(e.Source, source))
            .OrderByDescending(e => e.PriorityWeight)
            .ThenByDescending(e => e.HitCount)
            .FirstOrDefault();
    }

    /// <summary>找出所有"同一原文、不同译法"的冲突，并按严重程度（同优先级优先）排序。</summary>
    public static IReadOnlyList<GlossaryConflict> Detect(IEnumerable<GlossaryEntry> entries)
    {
        var result = new List<GlossaryConflict>();
        if (entries == null) return result;

        var groups = entries
            .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Source))
            .GroupBy(e => Normalize(e.Source), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            // 同一原文下不同译法才算冲突；译法相同只是重复条目，不算冲突
            var distinctTargets = group
                .Select(e => Normalize(e.Target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinctTargets < 2) continue;

            var ordered = group
                .OrderByDescending(e => e.PriorityWeight)
                .ThenByDescending(e => e.HitCount)
                .ToList();

            var a = ordered[0];
            var b = ordered[1];

            bool tie = a.PriorityWeight == b.PriorityWeight;
            result.Add(new GlossaryConflict
            {
                Source = group.First().Source.Trim(),
                CandidateA = a,
                CandidateB = b,
                NeedsConfirmation = tie,
                Suggestion = tie ? "请确认" : $"使用{a.SourceText}"
            });
        }

        return result
            .OrderByDescending(c => c.NeedsConfirmation)
            .ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 把一批术语按优先级去重：同原文只保留优先级最高的译法。
    /// 供翻译流水线在建术语占位符时使用，保证"用户覆盖企业、企业覆盖系统"。
    /// </summary>
    public static IReadOnlyList<GlossaryEntry> ResolveEffective(IEnumerable<GlossaryEntry> entries)
    {
        if (entries == null) return Array.Empty<GlossaryEntry>();

        return entries
            .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Source))
            .GroupBy(e => Normalize(e.Source), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.PriorityWeight)
                          .ThenByDescending(e => e.HitCount)
                          .First())
            .ToList();
    }

    /// <summary>命中计数：术语参与翻译后调用（界面统计卡要显示命中次数）。</summary>
    public static void MarkHit(GlossaryEntry entry, DateTime? at = null)
    {
        if (entry == null) return;
        entry.HitCount++;
        entry.LastHitAt = at ?? DateTime.UtcNow;
    }

    private static bool SameTerm(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value)
        => (value ?? string.Empty).Trim();
}
