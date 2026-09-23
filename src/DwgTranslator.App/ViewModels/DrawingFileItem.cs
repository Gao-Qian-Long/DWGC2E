using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Tasks;
using System.IO;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// One imported drawing in the multi-file workspace — the row of the task table.
///
/// The workspace used to be a flat list of text entities, which told the user nothing about the
/// drawings they had queued: the product's whole point is batch throughput, so the primary table is
/// per drawing (status / text count / progress / elapsed), as in the design.
///
/// 重构后这一行只是<b>任务层的视图</b>：<see cref="AttachTask"/> 挂上 TranslationTask 之后，
/// 状态/进度/耗时全部直接读任务对象（阶段名也是任务层给的 10 态），不再由实体状态反推。
/// 只有在没有任务（例如仅从 Excel 导入了译文、或实体表被单独使用）时才退回旧的推算逻辑。
/// </summary>
public partial class DrawingFileItem : ObservableObject
{
    public DrawingFileItem(string fullPath, string? importError = null)
    {
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);
        ImportError = importError ?? string.Empty;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string ImportError { get; }

    /// <summary>Row number in the task table; assigned when the workspace is refreshed.</summary>
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _isIncludedForExport = true;
    [ObservableProperty] private int _entityCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;

    /// <summary>First moment this drawing had any translated text (used for the elapsed column).</summary>
    private DateTime? _startedUtc;
    private DateTime? _completedUtc;

    /// <summary>任务层的任务对象；非空时本行是它的视图（其余字段只作兜底）。</summary>
    private TranslationTask? _task;

    #region 任务层绑定

    /// <summary>挂上/替换任务对象，并刷新所有派生属性。</summary>
    public void AttachTask(TranslationTask? task)
    {
        _task = task;
        OnPropertyChanged(nameof(Task));
        OnPropertyChanged(nameof(CreatedAtText));
        OnPropertyChanged(nameof(HasTask));
        RefreshFromTask();
    }

    public TranslationTask? Task => _task;

    public bool HasTask => _task != null;
    public string CreatedAtText => _task == null ? "—" : FormatDateTime(_task.CreatedAt);

    /// <summary>任务层推进后调用：把所有依赖任务的列一次性通知给界面。</summary>
    public void RefreshFromTask()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WorkflowStatusText));
        OnPropertyChanged(nameof(IsTranslationSuccessful));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(NeedsExport));
        OnPropertyChanged(nameof(NeedsPendingExport));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(TextCountText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(LastExportPath));
        OnPropertyChanged(nameof(OutputPath));
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(BatchResultText));
        OnPropertyChanged(nameof(RetryCountText));
        OnPropertyChanged(nameof(NextActionText));
        OnPropertyChanged(nameof(StartedAtText));
        OnPropertyChanged(nameof(TranslationCompletedAtText));
        OnPropertyChanged(nameof(ReviewCompletedAtText));
        OnPropertyChanged(nameof(LastExportedAtText));
        OnPropertyChanged(nameof(ElapsedDisplayText));
        OnPropertyChanged(nameof(OutputPathText));
        OnPropertyChanged(nameof(CanOpenProofreading));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanExport));
    }

    #endregion

    /// <summary>
    /// Task state in the wording the design asks for.
    /// 有任务对象时用任务层的 10 态；否则退回从实体状态推导的老口径。
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImportError)) return "导入失败";
            if (_task != null) return _task.StatusText;
            if (EntityCount == 0) return "无文本";
            if (CompletedCount >= EntityCount) return FailedCount > 0 ? "完成（含失败）" : "已完成";
            if (FailedCount > 0 && CompletedCount + FailedCount >= EntityCount) return "部分失败";
            if (CompletedCount > 0) return "翻译中";
            return "等待中";
        }
    }

    /// <summary>
    /// 流程状态：只表达"离交付物还差什么"。
    /// 用户批注（2026-09-22）：「整体的翻译校对导出逻辑也很奇怪…不要让用户都不知道怎么操作！校对不是必须的。」
    /// 原来翻成两道门：翻译完成显示"待校对"，必须先校对保存才变成"待导出"——于是翻译完的行只有一个
    /// 校对入口，想直接出图的人找不到路。现在校对降级为可选旁路，「翻译完成」与「已校对」都直接显示
    /// "待导出"，因为两者此刻都能立即导出（判定见 <see cref="NeedsExport"/>）。
    /// </summary>
    public string WorkflowStatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImportError)) return "导入失败";
            if (HasOutput) return "已导出";
            if (_task?.Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed) return "待导出";
            return StatusText;
        }
    }

    public bool IsTranslationSuccessful => HasOutput
        || _task?.Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed
        || _task == null && IsFinished && !HasError;

    /// <summary>
    /// 统计口径的「未校对」：翻译完成、**还没人工校对、也还没导出**。
    /// 用户批注（2026-09-23）：「导出后不再计未校对」——原来只判 task 状态，于是同一行会同时是
    /// 「已导出」和「未校对」，统计与表格互相矛盾（截图证据：11 行全"已导出"，指标却写
    /// "未校对 11 / 待导出 0"）。加 !HasOutput 后 未校对 / 待导出 / 已导出 三格互斥、导出即清零。
    /// 它仍然**不参与**导出可用性判定（那是 <see cref="NeedsExport"/>）。
    /// </summary>
    public bool NeedsReview => !HasOutput && _task?.Status == TranslationTaskStatus.ReadyForReview;

    /// <summary>
    /// 统计口径的「待导出」：**已人工校对**但还没输出。
    /// 与 <see cref="NeedsReview"/> 互斥（都带 !HasOutput，task 状态一个 Completed、一个 ReadyForReview）。
    /// 与 <see cref="NeedsExport"/> 刻意分开：那个是"能不能导出"的能力判定（两者都算），
    /// 用在按钮可用性与"下一步"提示上；这里是"统计应该记在哪一格"。
    /// </summary>
    public bool NeedsPendingExport => !HasOutput && _task?.Status == TranslationTaskStatus.Completed;

    /// <summary>
    /// 「清空已结束任务」可以回收的行：已经有输出（已导出）或翻译失败。
    /// 刻意**不用** TranslationTask.IsFinished —— 它把 ReadyForReview 也算"已结束"
    /// （TranslationTask.cs:133），那会连"翻译完了还没导出"的行一起清掉，而那正是用户要回来继续处理的一批。
    /// 已暂停（可能是主动暂停、准备继续）同样保留。
    /// </summary>
    public bool IsCleanupFinished => HasOutput || _task?.Status == TranslationTaskStatus.Failed;

    /// <summary>
    /// 能否导出。判定只要求"翻译已产出内容且还没有输出文件"，**不再要求先校对**：
    /// 用户批注（2026-09-22）「校对不是必须的」。原来只有 Completed（保存过校对）才成立，
    /// 导致翻译完成的行没有导出入口。导出链路本身对状态没有检查（MainViewModel.ImportExport），
    /// 唯一的门槛就是这里与 <see cref="CanExport"/>，所以解耦点只在这一处。
    /// </summary>
    public bool NeedsExport => !HasOutput && (_task?.Status
        is TranslationTaskStatus.Completed or TranslationTaskStatus.ReadyForReview
        || _task == null && IsFinished && !HasError);
    public string LastUpdatedText => FormatDateTime(_task?.UpdatedAt);

    public string StartedAtText => FormatDateTime(_task?.StartedAt);

    public string TranslationCompletedAtText => FormatDateTime(_task?.CompletedAt);

    public string ReviewCompletedAtText => FormatDateTime(_task?.ReviewCompletedAt);

    public string LastExportedAtText => FormatDateTime(_task?.LastExportedAt);

    public string RetryCountText => _task == null ? "—" : _task.RetryCount.ToString("N0");

    public string BatchResultText
    {
        get
        {
            if (_task == null)
                return EntityCount == 0 ? "尚未统计" : $"已完成 {CompletedCount:N0}/{EntityCount:N0}";
            if (_task.TextCount <= 0) return _task.FailedCount > 0 ? $"失败 {_task.FailedCount:N0}" : "尚未统计";
            return $"已译 {_task.TranslatedCount:N0}/{_task.TextCount:N0}"
                + (_task.FailedCount > 0 ? $" · 失败 {_task.FailedCount:N0}" : string.Empty);
        }
    }

    public string NextActionText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImportError)) return "修复源文件后重新添加";
            if (HasOutput) return "打开输出；如有需要可再次校对";
            if (_task == null) return "开始翻译";
            return _task.Status switch
            {
                TranslationTaskStatus.Parsing or TranslationTaskStatus.Extracting
                    or TranslationTaskStatus.Translating or TranslationTaskStatus.LayoutOptimizing
                    or TranslationTaskStatus.Writing => "等待当前阶段完成",
                // 翻译完成即可导出；校对是可选旁路，不再写成"必须先校对"。
                TranslationTaskStatus.ReadyForReview => "导出以生成输出图纸（校对可选）",
                TranslationTaskStatus.Completed => "导出以生成输出图纸",
                TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted => "查看错误并重试",
                TranslationTaskStatus.Paused => "继续任务",
                TranslationTaskStatus.Cancelled => "重新提交任务",
                TranslationTaskStatus.Skipped => "确认是否需要重新处理",
                _ => "开始翻译"
            };
        }
    }

    public bool CanOpenProofreading => HasOutput
        || _task?.Status is TranslationTaskStatus.ReadyForReview or TranslationTaskStatus.Completed;

    public bool CanRetry => _task?.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted;

    public bool CanExport => NeedsExport;

    /// <summary>0..100 — share of this drawing's entities that finished.</summary>
    public double ProgressPercent => _task?.Progress
        ?? (EntityCount == 0 ? 0 : Math.Min(100, CompletedCount * 100.0 / EntityCount));

    public string ProgressText => EntityCount == 0 && _task == null ? "0%" : $"{ProgressPercent:0}%";

    /// <summary>
    /// Wall-clock time spent on this drawing, from the first translated entity to the last one;
    /// empty while the drawing is still being worked on or has not started.
    /// </summary>
    public string ElapsedText
    {
        get
        {
            if (_task != null) return _task.ElapsedText;
            if (_startedUtc == null) return string.Empty;
            var end = _completedUtc ?? (CompletedCount > 0 && CompletedCount < EntityCount ? DateTime.UtcNow : null);
            if (end == null) return string.Empty;
            var span = end.Value - _startedUtc.Value;
            return span.TotalHours >= 1 ? span.ToString(@"hh\:mm\:ss") : span.ToString(@"mm\:ss");
        }
    }

    /// <summary>True while this drawing is being processed (drives the status colour in the table).</summary>
    public string ElapsedDisplayText => string.IsNullOrWhiteSpace(ElapsedText) ? "—" : ElapsedText;

    public bool IsActive => _task?.IsActive ?? (CompletedCount > 0 && CompletedCount < EntityCount);

    /// <summary>True when the task layer is done with this drawing (done / failed / cancelled).</summary>
    public bool IsFinished => _task?.IsFinished ?? (EntityCount > 0 && CompletedCount >= EntityCount);

    public string TextCountText
    {
        get
        {
            if (_task != null && _task.TextCount > 0) return _task.TextCount.ToString("N0");
            return EntityCount == 0 ? "—" : EntityCount.ToString("N0");
        }
    }

    public string PriorityText => _task?.PriorityText ?? "普通";

    /// <summary>Failure reason shown in the tooltip (task error first — it is the more recent one).</summary>
    public string ErrorText => !string.IsNullOrWhiteSpace(_task?.Error) ? _task!.Error!
        : ImportError;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>最近一次成功导出的文件路径（项目导出历史的 UI 缓存）。</summary>
    public string? LastExportPath => _task?.LastExportPath;

    /// <summary>
    /// Display compatibility for older task records. New records use <see cref="LastExportPath"/>;
    /// the obsolete task field is only exposed when a pre-migration task has no new cache yet.
    /// </summary>
    public string? OutputPath => LastExportPath ?? _task?.OutputPath;

    public string OutputText => string.IsNullOrWhiteSpace(OutputPath)
        ? string.Empty
        : Path.GetFileName(OutputPath);

    /// <summary>
    /// Live filesystem probe on purpose: the UI smoke contract asserts that deleting the exported
    /// file clears the action without any refresh (<c>UiSmoke/Program.cs:279-280</c>), so a cached
    /// result would be a behaviour regression, not an optimisation.
    /// </summary>
    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPath) && File.Exists(OutputPath);

    public string OutputPathText => string.IsNullOrWhiteSpace(OutputPath) ? "尚未导出" : OutputPath!;

    public string SummaryText => _task != null
        ? $"{_task.TextCount} 条 · 已译 {_task.TranslatedCount} · 失败 {_task.FailedCount}"
            + (HasOutput ? $" · 输出 {OutputText}" : string.Empty)
        : string.IsNullOrWhiteSpace(ImportError)
            ? $"{EntityCount} 条 · 已完成 {CompletedCount} · 失败 {FailedCount}"
            : $"导入失败 · {ImportError}";

    public void Refresh(IEnumerable<TextEntity> entities)
    {
        var mine = entities.Where(e => string.Equals(
            Normalize(e.SourceFilePath), FullPath, StringComparison.OrdinalIgnoreCase)).ToList();

        EntityCount = mine.Count;
        CompletedCount = mine.Count(e => e.Status is TranslationStatus.Translated
            or TranslationStatus.Reviewed or TranslationStatus.WritebackSuccess);
        FailedCount = mine.Count(e => e.Status is TranslationStatus.TranslationFailed
            or TranslationStatus.WritebackFailed);

        // Stamp the elapsed window from the first finished entity to the last one. Only used when the
        // row has no task yet (e.g. Excel-only sessions); with a task the task object owns timing.
        if (CompletedCount > 0 && _startedUtc == null) _startedUtc = DateTime.UtcNow;
        if (EntityCount > 0 && CompletedCount >= EntityCount) _completedUtc ??= DateTime.UtcNow;
        if (CompletedCount == 0) { _startedUtc = null; _completedUtc = null; }

        RefreshFromTask();
    }


    /// <summary>Clears the timing window, e.g. after the workspace is cleared.</summary>
    public void ResetTiming()
    {
        _startedUtc = null;
        _completedUtc = null;
        OnPropertyChanged(nameof(ElapsedText));
    }

    private static string FormatDateTime(DateTime? value)
        => value is { } timestamp && timestamp != default
            ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";

    private static string FormatDateTime(DateTime value)
        => value == default ? "—" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
