using System;

namespace DwgTranslator.Core.Models;

/// <summary>
/// 术语来源。优先级链：用户术语 &gt; 企业术语 &gt; 系统术语 &gt; AI 默认（提示词 §8）。
/// 冲突时不得静默覆盖——由 <see cref="DwgTranslator.Core.Services.GlossaryConflictDetector"/>
/// 给出候选与建议，交由用户确认。
/// </summary>
public enum GlossarySource
{
    /// <summary>系统内置术语（最低优先级，可被用户/企业覆盖）。</summary>
    System = 0,
    /// <summary>企业术语库（团队统一口径）。</summary>
    Enterprise = 1,
    /// <summary>用户自定义术语（最高优先级）。</summary>
    User = 2
}

/// <summary>
/// A glossary entry for deterministic term replacement.
/// </summary>
public class GlossaryEntry
{
    public string? CloudId { get; set; }
    public string? CloudNote { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>本地术语文件夹；为空表示未分类。</summary>
    public string Folder { get; set; } = string.Empty;

    // ── 以下为新增字段（向后兼容：旧术语文件缺这些字段时取默认值）──

    /// <summary>来源：用户 / 企业 / 系统。默认按"系统"处理，避免旧文件被误判成用户术语。</summary>
    public GlossarySource SourceKind { get; set; } = GlossarySource.System;

    /// <summary>语言方向，例如 "ZH-EN"；为空表示不限方向。</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>命中次数（翻译时累加，用于术语统计卡）。</summary>
    public int HitCount { get; set; }

    /// <summary>最近命中时间。</summary>
    public DateTime? LastHitAt { get; set; }

    /// <summary>条目是否启用（禁用后不参与替换，但保留记录）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>来源中文名（界面直接绑定，避免每个视图各写一套映射）。</summary>
    public string SourceText => SourceKind switch
    {
        GlossarySource.User => "用户术语",
        GlossarySource.Enterprise => "企业术语",
        _ => "系统术语"
    };

    /// <summary>状态中文名。</summary>
    public string StatusText => Enabled ? "启用" : "停用";

    /// <summary>数字形式的优先级权重（越大越优先），用于冲突裁决与排序。</summary>
    public int PriorityWeight => (int)SourceKind;

    public GlossaryEntry Clone() => new()
    {
        CloudId = CloudId,
        CloudNote = CloudNote,
        Source = Source,
        Target = Target,
        Category = Category,
        Folder = Folder,
        SourceKind = SourceKind,
        Direction = Direction,
        HitCount = HitCount,
        LastHitAt = LastHitAt,
        Enabled = Enabled
    };

    public override string ToString() => $"{Source} → {Target} ({SourceText})";
}
