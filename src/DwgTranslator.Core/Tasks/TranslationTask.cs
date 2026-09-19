using System;
using System.Collections.Generic;

namespace DwgTranslator.Core.Tasks;

/// <summary>
/// 任务阶段。取值刻意细化到用户能理解的具体步骤——设计稿与提示词都要求
/// 「让用户知道具体阶段」，禁止再用"处理中 / 运行中"这类模糊词。
/// </summary>
public enum TranslationTaskStatus
{
    Pending,
    Parsing,
    Extracting,
    Translating,
    LayoutOptimizing,
    Writing,
    ReadyForReview,
    Completed,
    Failed,
    Cancelled,
    Paused,
    PartiallyCompleted,
    Skipped
}

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

/// <summary>
/// 一个图纸翻译任务。这是任务层的核心模型：UI 只读它、TaskManager 只写它，
/// 双方的契约就是这一组字段（Id / 状态 / 进度 / 计数 / 耗时 / 错误 / 时间戳 / 优先级）。
/// </summary>
public sealed class TranslationTask
{
    public const int CurrentSchemaVersion = 2;
    public TranslationTask(string filePath, TaskPriority priority = TaskPriority.Normal)
    {
        Id = Guid.NewGuid().ToString("N");
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        Priority = priority;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = CreatedAt;
        StatusChangedAt = CreatedAt;
    }

    public string Id { get; set; }
    public string FilePath { get; set; }
    public string FileName { get; set; }

    public TranslationTaskStatus Status { get; set; } = TranslationTaskStatus.Pending;

    /// <summary>Schema version of this persisted task row. Version 1 is the pre-state-machine format.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>State immediately before <see cref="Status"/>.</summary>
    public TranslationTaskStatus PreviousStatus { get; set; } = TranslationTaskStatus.Pending;

    /// <summary>Stable machine-readable reason for the latest state change.</summary>
    public TranslationTaskTransitionReason LastTransitionReason { get; set; } = TranslationTaskTransitionReason.Created;

    /// <summary>UTC timestamp of the latest state change.</summary>
    public DateTime StatusChangedAt { get; set; }

    /// <summary>0..100，按已译文本占该图纸可译文本的比例推进。</summary>
    public double Progress { get; set; }

    /// <summary>该图纸提取到的可译文本条数。</summary>
    public int TextCount { get; set; }

    /// <summary>已翻译（含已校对）条数。</summary>
    public int TranslatedCount { get; set; }

    /// <summary>失败条数；单条失败不终止整个任务，全部失败才置 Failed。</summary>
    public int FailedCount { get; set; }

    /// <summary>排版阶段统计：为放入可用空间而缩小字号的次数。</summary>
    public int AutoScaledLabelCount { get; set; }

    /// <summary>排版阶段统计：通过缩小字号解决干涉的次数。</summary>
    public int InterferenceResolvedCount { get; set; }

    /// <summary>Worker 翻译计费上下文（online/offline）；与后续 CAD 写回模式无关。</summary>
    public string? BillingMode { get; set; }
    /// <summary>关联的翻译项目。输出路径由项目导出历史管理。</summary>
    public string? ProjectId { get; set; }
    /// <summary>最近一次成功导出的文件路径，仅作为任务列表的快速显示/打开缓存。</summary>
    /// <remarks>权威记录仍然是翻译项目的 ExportHistory。</remarks>
    public string? LastExportPath { get; set; }
    [Obsolete("输出路径已迁移到翻译项目的导出历史。仅为旧任务记录兼容保留。")]
    public string? OutputPath { get; set; }

    public string CheckpointSignature { get; set; } = string.Empty;
    public List<DwgTranslator.Core.Models.TranslationPair> SuccessfulTranslations { get; set; } = new();

    public TaskPriority Priority { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>最近一次状态、进度、校对或导出发生变化的时间。</summary>
    public DateTime UpdatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    /// <summary>翻译流水线结束时间；待校对任务也会记录，不能等同于校对完成。</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>用户成功保存校对并将任务推进到待导出的时间。</summary>
    public DateTime? ReviewCompletedAt { get; set; }

    /// <summary>最近一次成功导出图纸的时间。</summary>
    public DateTime? LastExportedAt { get; set; }

    /// <summary>用户主动重试该图纸的次数。</summary>
    public int RetryCount { get; set; }

    public TimeSpan Elapsed => StartedAt == null
        ? TimeSpan.Zero
        : (CompletedAt ?? DateTime.UtcNow) - StartedAt.Value;

    /// <summary>耗时文本（mm:ss 或 hh:mm:ss）；未开始时为空。</summary>
    public string ElapsedText => StartedAt == null
        ? string.Empty
        : (Elapsed.TotalHours >= 1 ? Elapsed.ToString(@"hh\:mm\:ss") : Elapsed.ToString(@"mm\:ss"));

    public bool IsFinished => Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed
        or TranslationTaskStatus.Failed or TranslationTaskStatus.Cancelled
        or TranslationTaskStatus.PartiallyCompleted or TranslationTaskStatus.Skipped;

    public bool IsActive => Status is TranslationTaskStatus.Parsing
        or TranslationTaskStatus.Extracting or TranslationTaskStatus.Translating
        or TranslationTaskStatus.LayoutOptimizing or TranslationTaskStatus.Writing;

    /// <summary>状态中文名（UI 直接绑定，避免每个视图各写一套映射）。</summary>
    public string StatusText => Status switch
    {
        TranslationTaskStatus.Pending => "等待中",
        TranslationTaskStatus.Parsing => "解析中",
        TranslationTaskStatus.Extracting => "提取文本",
        TranslationTaskStatus.Translating => "翻译中",
        TranslationTaskStatus.LayoutOptimizing => "排版优化",
        TranslationTaskStatus.Writing => "写回中",
        TranslationTaskStatus.ReadyForReview => "待校对",
        TranslationTaskStatus.Completed => "翻译完成",
        TranslationTaskStatus.Failed => "失败",
        TranslationTaskStatus.Cancelled => "已取消",
        TranslationTaskStatus.Paused => "已暂停",
        TranslationTaskStatus.PartiallyCompleted => "部分完成",
        TranslationTaskStatus.Skipped => "已跳过",
        _ => "等待中"
    };

    public string PriorityText => Priority switch
    {
        TaskPriority.High => "高",
        TaskPriority.Low => "低",
        _ => "普通"
    };

    /// <summary>兼容旧任务记录：补齐首版任务文件中没有的审计时间。</summary>
    public void NormalizeAuditMetadata()
    {
        if (CreatedAt == default)
            CreatedAt = StartedAt ?? CompletedAt ?? ReviewCompletedAt ?? LastExportedAt ?? DateTime.UtcNow;

        if (UpdatedAt == default)
            UpdatedAt = LastExportedAt ?? ReviewCompletedAt ?? CompletedAt ?? StartedAt ?? CreatedAt;

        if (UpdatedAt < CreatedAt) UpdatedAt = CreatedAt;
        if (StatusChangedAt == default) StatusChangedAt = UpdatedAt;
        if (StatusChangedAt < CreatedAt) StatusChangedAt = CreatedAt;
        if (!Enum.IsDefined(PreviousStatus)) PreviousStatus = TranslationTaskStatus.Pending;
        if (!Enum.IsDefined(LastTransitionReason)) LastTransitionReason = TranslationTaskTransitionReason.LegacyMigration;
        SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>恢复上次运行遗留的任务（任务恢复）：清掉运行态，回到可重跑的状态。</summary>
    public void ResetForResume()
    {
        if (Status is TranslationTaskStatus.Completed or TranslationTaskStatus.PartiallyCompleted or TranslationTaskStatus.Skipped) return;
        this.TransitionTo(TranslationTaskStatus.Pending, TranslationTaskTransitionReason.RecoveredAfterInterruption);
        Progress = 0;
        StartedAt = null;
        CompletedAt = null;
        Error = null;
    }

    public override string ToString() => $"{FileName} [{StatusText}]";
}
