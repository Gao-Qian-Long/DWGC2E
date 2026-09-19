using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.IO;
using System.Diagnostics;
using DwgTranslator.Core.Tasks;
using DwgTranslator.Core.Services;
using Serilog;

namespace DwgTranslator.App.ViewModels;

// Presentation-only state. No task execution, persistence or API contracts live here.
public partial class MainViewModel
{
    [ObservableProperty] private string _batchSearch = "";
    [ObservableProperty] private int _batchStatusFilter;
    [ObservableProperty] private int _batchDateFilter;
    [ObservableProperty] private DrawingFileItem? _selectedBatchTask;
    [ObservableProperty] private bool _isTaskDetailOpen;
    [ObservableProperty] private bool _isProofreading;
    private ListCollectionView? _batchView;
    private readonly HashSet<DrawingFileItem> _batchRows = new();
    public ICollectionView BatchView => _batchView ??= CreateBatchView();
    /// <summary>
    /// 状态栏文本。等待状态下把"输出到哪里 / 队列里有多少张"放进状态栏——这属于外壳的常驻信息，
    /// 放在页面主体里会让页面骨架随状态变化而重排（用户截图里那条 "共 0 张图纸 … exports"）。
    /// </summary>
    public string ShellStatusText
    {
        get
        {
            if (IsProcessing) return StatusMessage;
            if (!HasDrawingFiles) return "就绪";
            return string.IsNullOrWhiteSpace(_config.ExportDirectory)
                ? $"就绪 — {DrawingFiles.Count} 张图纸 · 输出随图纸保存"
                : $"就绪 — {DrawingFiles.Count} 张图纸 · 输出到 {_config.ExportDirectory}";
        }
    }

    public int WorkspaceFileCount => DrawingFiles.Count;
    public int WorkspaceActiveCount => DrawingFiles.Count(x => x.IsActive);
    public int WorkspaceSuccessfulCount => DrawingFiles.Count(x => x.IsTranslationSuccessful);
    public int WorkspaceFailedCount => DrawingFiles.Count(IsFailedTask);
    public int WorkspaceReviewCount => DrawingFiles.Count(x => x.NeedsReview);
    public int WorkspacePendingExportCount => DrawingFiles.Count(x => x.NeedsExport);
    public int WorkspaceExportedCount => DrawingFiles.Count(x => x.HasOutput);
    public double WorkspaceOverallProgress => DrawingFiles.Count == 0
        ? 0
        : Math.Clamp(DrawingFiles.Average(x => x.ProgressPercent), 0, 100);
    public string WorkspaceProgressText => $"{WorkspaceOverallProgress:0}%";
    public string WorkspaceRunStateText => IsExporting
        ? "正在导出"
        : IsTranslating || _taskManager.IsRunning
            ? (IsCancellationRequested ? "正在停止" : "正在翻译")
            : WorkspaceFailedCount > 0
                ? "需要处理"
                : WorkspaceReviewCount > 0
                    ? "等待校对"
                    : WorkspacePendingExportCount > 0
                        ? "等待导出"
                        : WorkspaceExportedCount > 0 && WorkspaceExportedCount == WorkspaceFileCount
                            ? "已全部导出"
                            : HasDrawingFiles ? "就绪" : "未添加图纸";
    public string WorkspaceNextStepText
    {
        get
        {
            if (!HasDrawingFiles) return "下一步：添加 DWG / DXF 图纸。";
            if (IsTranslating || WorkspaceActiveCount > 0) return $"正在处理 {Math.Max(1, WorkspaceActiveCount)} 张图纸，请等待队列完成。";
            if (WorkspaceFailedCount > 0) return $"下一步：先重试 {WorkspaceFailedCount} 张失败图纸，或在批量任务中查看原因。";
            if (WorkspaceReviewCount > 0) return $"下一步：校对 {WorkspaceReviewCount} 张翻译结果；保存后才进入待导出。";
            if (WorkspacePendingExportCount > 0) return $"下一步：导出 {WorkspacePendingExportCount} 张已校对图纸。";
            if (WorkspaceExportedCount > 0) return $"已导出 {WorkspaceExportedCount} 张图纸，可继续添加新图纸。";
            return CanStartWorkspaceTranslation ? "下一步：开始翻译。" : "当前队列没有可执行任务。";
        }
    }
    public bool CanStartWorkspaceTranslation => !IsProcessing && !IsTranslating && !IsExporting
        && DrawingFiles.Any(x => x.Task == null || x.Task.Status is TranslationTaskStatus.Pending or TranslationTaskStatus.Paused);
    public bool CanRetryFailedDrawingTasks => !IsProcessing && !IsTranslating && !IsExporting && HasFailedDrawingTasks;
    public bool CanExportWorkspace => !IsProcessing && !IsTranslating && !IsExporting
        && DrawingFiles.Any(x => x.IsIncludedForExport && x.NeedsExport);

    /// <summary>
    /// 输出目录的人话版本。未导出过时说明默认规则（落到源图纸旁边），导出过一次后显示真实目录，
    /// 所以这一行在"还没有目录"时也不是空白（空白会让整块区域上下跳动）。
    /// </summary>
    public string WorkspaceOutputLocationText => string.IsNullOrWhiteSpace(_config.ExportDirectory)
        ? $"随图纸保存：<图纸所在目录>\\{DefaultOutputFolderName}"
        : _config.ExportDirectory;

    /// <summary>
    /// 操作条右侧的常驻说明。它永远不会返回空串——空串会让那一格随状态出现/消失，
    /// 整条操作栏的宽度就会跳（这是用户截图里"共 0 张图纸…exports"那条的成因）。
    /// </summary>
    public string WorkspaceActionHintText => DrawingFiles.Count == 0
        ? "添加图纸后即可开始翻译"
        : string.IsNullOrWhiteSpace(_config.ExportDirectory)
            ? $"共 {DrawingFiles.Count} 张图纸 · 输出随图纸保存"
            : $"共 {DrawingFiles.Count} 张图纸 · 输出到 {_config.ExportDirectory}";

    private void RaiseWorkspaceSummaryProperties()
    {
        OnPropertyChanged(nameof(WorkspaceFileCount));
        OnPropertyChanged(nameof(WorkspaceActiveCount));
        OnPropertyChanged(nameof(WorkspaceSuccessfulCount));
        OnPropertyChanged(nameof(WorkspaceFailedCount));
        OnPropertyChanged(nameof(WorkspaceReviewCount));
        OnPropertyChanged(nameof(WorkspacePendingExportCount));
        OnPropertyChanged(nameof(WorkspaceExportedCount));
        OnPropertyChanged(nameof(WorkspaceOverallProgress));
        OnPropertyChanged(nameof(WorkspaceProgressText));
        OnPropertyChanged(nameof(WorkspaceRunStateText));
        OnPropertyChanged(nameof(WorkspaceNextStepText));
        OnPropertyChanged(nameof(WorkspaceActionHintText));
        OnPropertyChanged(nameof(WorkspaceOutputLocationText));
        // 状态栏也带队列张数与输出目录，任何工作区状态变化都要重算它。
        OnPropertyChanged(nameof(ShellStatusText));
        OnPropertyChanged(nameof(CanStartWorkspaceTranslation));
        OnPropertyChanged(nameof(CanRetryFailedDrawingTasks));
        OnPropertyChanged(nameof(CanExportWorkspace));
    }

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShellStatusText));
        RaiseWorkspaceSummaryProperties();
    }
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(ShellStatusText));
    partial void OnIsTranslatingChanged(bool value) => RaiseWorkspaceSummaryProperties();
    partial void OnIsExportingChanged(bool value) => RaiseWorkspaceSummaryProperties();
    partial void OnProgressValueChanged(double value) => RaiseWorkspaceSummaryProperties();
    partial void OnHasDrawingFilesChanged(bool value) => RaiseWorkspaceSummaryProperties();
    partial void OnTranslatedCountChanged(int value) => RaiseWorkspaceSummaryProperties();
    partial void OnFailedCountChanged(int value) => RaiseWorkspaceSummaryProperties();
    partial void OnIsCancellationRequestedChanged(bool value) => RaiseWorkspaceSummaryProperties();
    public bool HasFailedDrawingTasks => DrawingFiles.Any(IsFailedTask);
    public int BatchTotalCount => DrawingFiles.Count;
    public int BatchActiveCount => DrawingFiles.Count(x => x.IsActive);
    public int BatchPendingCount => DrawingFiles.Count(x => x.Task?.Status == TranslationTaskStatus.Pending
        || x.Task == null && !x.IsActive && !x.IsFinished && !x.HasError);
    public int BatchReviewCount => DrawingFiles.Count(x => x.NeedsReview);
    public int BatchPendingExportCount => DrawingFiles.Count(x => x.NeedsExport);
    public int BatchExportedCount => DrawingFiles.Count(x => x.HasOutput);
    public int BatchCompletedCount => BatchPendingExportCount + BatchExportedCount;
    public int BatchFailedCount => DrawingFiles.Count(IsFailedTask);
    public string BatchCountText => $"全部 {BatchTotalCount}    运行中 {BatchActiveCount}    待处理 {BatchPendingCount}    待校对 {BatchReviewCount}    待导出 {BatchPendingExportCount}    已导出 {BatchExportedCount}    失败 {BatchFailedCount}";
    private void RaiseBatchSummaryProperties()
    {
        OnPropertyChanged(nameof(BatchTotalCount));
        OnPropertyChanged(nameof(BatchActiveCount));
        OnPropertyChanged(nameof(BatchPendingCount));
        OnPropertyChanged(nameof(BatchReviewCount));
        OnPropertyChanged(nameof(BatchCompletedCount));
        OnPropertyChanged(nameof(BatchPendingExportCount));
        OnPropertyChanged(nameof(BatchExportedCount));
        OnPropertyChanged(nameof(BatchFailedCount));
        OnPropertyChanged(nameof(BatchCountText));
        OnPropertyChanged(nameof(HasFailedDrawingTasks));
    }
    private ListCollectionView CreateBatchView()
    {
        var view = new ListCollectionView(DrawingFiles) { Filter = MatchBatch };
        DrawingFiles.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                foreach (var row in _batchRows) PropertyChangedEventManager.RemoveHandler(row, BatchRowChanged, "");
                _batchRows.Clear();
            }
            if (e.OldItems != null) foreach (DrawingFileItem row in e.OldItems)
                { PropertyChangedEventManager.RemoveHandler(row, BatchRowChanged, ""); _batchRows.Remove(row); }
            if (e.NewItems != null) foreach (DrawingFileItem row in e.NewItems)
                { if (_batchRows.Add(row)) PropertyChangedEventManager.AddHandler(row, BatchRowChanged, ""); }
            if (SelectedBatchTask != null && !DrawingFiles.Contains(SelectedBatchTask)) { SelectedBatchTask = null; IsTaskDetailOpen = false; IsProofreading = false; }
            RaiseBatchSummaryProperties();
        };
        foreach (var row in DrawingFiles) { _batchRows.Add(row); PropertyChangedEventManager.AddHandler(row, BatchRowChanged, ""); }
        return view;
    }
    private bool _batchRefreshQueued;
    private void BatchRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_batchRefreshQueued || e.PropertyName is not (nameof(DrawingFileItem.StatusText)
            or nameof(DrawingFileItem.WorkflowStatusText) or nameof(DrawingFileItem.HasOutput)
            or nameof(DrawingFileItem.NeedsExport) or nameof(DrawingFileItem.IsActive)
            or nameof(DrawingFileItem.HasError) or nameof(DrawingFileItem.LastUpdatedText))) return;
        _batchRefreshQueued = true;
        OnUiThread(() => { _batchRefreshQueued = false; RefreshBatch(); });
    }
    private static bool IsFailedTask(DrawingFileItem row) => row.HasError || row.Task?.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted;
    private bool MatchBatch(object value)
    {
        if (value is not DrawingFileItem row) return false;
        if (!row.FileName.Contains((BatchSearch ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (BatchDateFilter > 0)
        {
            if (row.Task == null) return false;
            var updatedAt = row.Task.UpdatedAt == default ? row.Task.CreatedAt : row.Task.UpdatedAt;
            if (updatedAt.ToLocalTime().Date < DateTime.Today.AddDays(BatchDateFilter == 1 ? 0 : -6)) return false;
        }
        return BatchStatusFilter switch
        {
            1 => row.IsActive,
            2 => row.Task?.Status == TranslationTaskStatus.Pending
                || row.Task == null && !row.IsActive && !row.IsFinished && !row.HasError,
            3 => row.NeedsReview,
            4 => row.NeedsExport,
            5 => row.HasOutput,
            6 => IsFailedTask(row),
            7 => row.Task?.Status == TranslationTaskStatus.Paused,
            8 => row.Task?.Status == TranslationTaskStatus.Cancelled,
            9 => row.Task?.Status == TranslationTaskStatus.Skipped,
            _ => true
        };
    }
    private void RefreshBatch() { _batchView?.Refresh(); RaiseBatchSummaryProperties(); }
    partial void OnBatchSearchChanged(string value) => RefreshBatch();
    partial void OnBatchStatusFilterChanged(int value) => RefreshBatch();
    partial void OnBatchDateFilterChanged(int value) => RefreshBatch();
    [RelayCommand] private void SelectAllDrawingOutputs() { foreach (var row in DrawingFiles) row.IsIncludedForExport = true; }
    [RelayCommand] private void ClearDrawingOutputs() { foreach (var row in DrawingFiles) row.IsIncludedForExport = false; }
    [RelayCommand] private void ShowTaskDetail(DrawingFileItem? row) { if (row == null) return; SelectedBatchTask = row; IsTaskDetailOpen = true; }
    [RelayCommand] private void CloseTaskDetail() => IsTaskDetailOpen = false;

    [RelayCommand]
    private void OpenTaskProofreading()
    {
        if (SelectedBatchTask == null) return;
        if (SelectedBatchTask.Task?.IsActive == true)
        {
            Services.ToastService.Warning("任务正在执行，请完成或停止后再校对。");
            return;
        }
        if (!SelectedBatchTask.CanOpenProofreading)
        {
            Services.ToastService.Warning(SelectedBatchTask.HasError
                ? "此任务尚未生成可校对译文，请先重试失败任务。"
                : "此任务尚未完成翻译，暂时不能进入校对。");
            return;
        }
        if (!ConfirmLeaveProofreading()) return;
        SelectedDrawingFile = SelectedBatchTask;
        ApplyFilter();
        IsProofreading = true;
        IsTaskDetailOpen = false;
    }

    [RelayCommand]
    private async Task ExportSelectedDrawingAsync(DrawingFileItem? item)
    {
        if (item == null || !item.CanExport || IsProcessing) return;
        var previousSelection = DrawingFiles.ToDictionary(row => row, row => row.IsIncludedForExport);
        try
        {
            foreach (var row in DrawingFiles) row.IsIncludedForExport = ReferenceEquals(row, item);
            await ExportDwgAsync();
        }
        finally
        {
            foreach (var pair in previousSelection) pair.Key.IsIncludedForExport = pair.Value;
        }
    }

    [RelayCommand] private void BackToTaskList() { if (ConfirmLeaveProofreading()) IsProofreading = false; }

    private readonly Dictionary<DwgTranslator.Core.Models.TextEntity, (string? Text, DwgTranslator.Core.Models.TranslationStatus Status)> _proofreadingOriginals = new();
    public bool HasUnsavedProofreading => _proofreadingOriginals.Count > 0;
    public void TrackProofreadingEdit(DwgTranslator.Core.Models.TextEntity entity)
    {
        _proofreadingWorkspaceVersion++;
        _proofreadingOriginals.TryAdd(entity, (entity.TranslatedText, entity.Status));
        OnPropertyChanged(nameof(HasUnsavedProofreading));
    }
    [RelayCommand]
    private void SaveProofreading()
    {
        TrySaveProofreading();
    }
    [RelayCommand]
    private void DiscardProofreading()
    {
        // §L4 丢弃全部未保存校对没有撤销入口，必须先二次确认（与 ClearAll / 批量替换一致）。
        if (HasUnsavedProofreading
            && !Views.ConfirmDialog.Ask(
                Application.Current?.MainWindow,
                "取消编辑",
                $"将丢弃 {_proofreadingOriginals.Count} 处未保存的校对更改，且无法撤销。确定取消编辑吗？",
                confirmText: "取消编辑",
                danger: true))
            return;

        DiscardProofreadingCore();
    }

    private void DiscardProofreadingCore()
    {
        foreach (var pair in _proofreadingOriginals) { pair.Key.TranslatedText = pair.Value.Text ?? ""; pair.Key.Status = pair.Value.Status; }
        _proofreadingOriginals.Clear();
        OnPropertyChanged(nameof(HasUnsavedProofreading));
        UpdateStatistics(); ApplyFilter();
        StatusMessage = "已取消未保存的校对更改。";
    }
    public bool ConfirmLeaveProofreading()
    {
        if (!HasUnsavedProofreading) return true;
        var answer = Views.PromptDialog.Show("校对更改尚未保存。是否保存到当前任务后继续？输出图纸仍需显式导出。", "未保存的校对", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return TrySaveProofreading();
        DiscardProofreadingCore();
        return true;
    }
    [RelayCommand]
    private void OpenDrawingOutput(DrawingFileItem? row)
    {
        var path = ResolveDrawingOutputPath(row);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusMessage = "输出文件尚未生成、已移动或已删除，请重新导出图纸。";
            return;
        }

        // Project export history is authoritative. Once a historical path is found, repair the
        // task cache so the task table and the next launch do not need to repeat the lookup.
        if (row?.Task != null && !string.Equals(row.Task.LastExportPath, path, StringComparison.OrdinalIgnoreCase))
        {
            try { _taskManager.RecordExportPath(row.Task.Id, path); }
            catch (Exception ex) { Log.Debug(ex, "回填任务导出路径失败 {TaskId}", row.Task.Id); }
        }

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "打开输出文件失败 {Path}", path); StatusMessage = "无法打开输出文件，请确认已安装对应的 CAD 软件，或从输出目录打开。"; }
    }

    /// <summary>
    /// Resolve output paths in migration-safe order:
    /// task cache → project export history → legacy task field.
    /// A stale latest history entry is skipped in favour of the newest existing export.
    /// </summary>
    private string? ResolveDrawingOutputPath(DrawingFileItem? row)
    {
        if (row == null) return null;

        var cached = row.Task?.LastExportPath;
        if (IsExistingFile(cached)) return cached;

        var project = ActiveTranslationProject;
        var projectId = row.Task?.ProjectId;
        if (project == null || (!string.IsNullOrWhiteSpace(projectId) && !string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                try { project = ProjectStore.Load(projectId); }
                catch (Exception ex) { Log.Debug(ex, "读取项目导出历史失败 {ProjectId}", projectId); }
            }
        }

        var historyPath = TranslationProjectExportLocator.FindLatestOutputPath(project, row.FullPath);
        if (IsExistingFile(historyPath)) return historyPath;

        // OutputPath is intentionally last: it is an obsolete field retained only for old tasks.
        var legacy = row.Task?.OutputPath;
        return IsExistingFile(legacy) ? legacy : null;
    }

    private static bool IsExistingFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
