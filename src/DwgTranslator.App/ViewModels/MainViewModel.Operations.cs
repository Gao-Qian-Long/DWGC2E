using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.App.Services;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Operations

    [RelayCommand]
    private void MarkAllReviewed()
    {
        if (IsProcessing || IsExporting) return;

        IEnumerable<TextEntity> scope = Entities;
        if (SelectedDrawingFile != null)
        {
            var selectedPath = NormalizeSourcePath(SelectedDrawingFile.FullPath);
            scope = scope.Where(entity => string.Equals(
                NormalizeSourcePath(entity.SourceFilePath), selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        int count = 0;
        foreach (var entity in scope.Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.GlossaryMatched))
        {
            TrackProofreadingEdit(entity);
            entity.Status = TranslationStatus.Reviewed;
            count++;
        }

        UpdateStatistics();
        ApplyFilter();
        StatusMessage = count > 0
            ? $"已将 {count} 条译文标记为已校对；请点击“保存更改”完成校对。"
            : "当前图纸没有待标记的译文。";
    }
    [RelayCommand]
    private void ClearAll()
    {
        if (IsProcessing || !ConfirmLeaveProofreading()) return;
        if (Entities.Count == 0 && DrawingFiles.Count == 0) return;

        // 危险操作走主题化确认框（§29）
        // 计数与 ClearAll 实际清空的两个集合同步：此前只报 Entities.Count（译文条目），
        // 队列里有图纸但未翻译时会显示"清空所有 0 条数据"，与用户所见不符。
        var confirmed = Views.ConfirmDialog.Ask(
            Application.Current?.MainWindow,
            Strings.Get("MsgClearConfirmTitle"),
            Strings.Get("MsgClearConfirmBody", Entities.Count, DrawingFiles.Count),
            confirmText: "清空", danger: true);
        if (!confirmed || !TryClearSavedProofreading()) return;

        Entities.Clear();
        FilteredEntities.Clear();
        DrawingFiles.Clear();
        InvalidateEntityIndex();
        // 清空工作区同时也清掉任务层记录（含未完成）：这是用户确认过的操作，
        // 且 IsProcessing 已经挡掉了"跑到一半还清列表"的情况。
        _taskManager.Clear(includeUnfinished: true);
        HasDrawingFiles = false;
        ProgressDetailText = string.Empty;
        SelectedDrawingFile = null;
        HasMultipleDrawingFiles = false;
        SelectedFilePath = string.Empty;
        TotalCount = 0; TranslatedCount = 0; FailedCount = 0;
        GlossaryHitCount = 0; CacheHitCount = 0; VisibleCount = 0;
        ProgressValue = 0;
        StatusMessage = Strings.Get("StatusCleared");
    }

    /// <summary>
    /// 把一张图纸移出队列（用户批注 2026-09-22：「文件队列怎么没有一些基础操作，比如删除队列里面的文件」）。
    /// 只从队列、实体表与任务记录里移除，不删除磁盘文件；翻译进行中时拒绝，避免与运行中的批次互踩。
    /// </summary>
    [RelayCommand]
    private void RemoveDrawing(DrawingFileItem? item)
    {
        if (item == null) return;
        if (IsProcessing) { ToastService.Warning("当前任务正在执行，完成或取消后再移除图纸。"); return; }
        if (item.Task?.IsActive == true) { ToastService.Warning("该图纸正在翻译，请先取消任务再移除。"); return; }

        var index = DrawingFiles.IndexOf(item);
        if (index < 0) return;
        if (item.Task != null) _taskManager.Remove(item.Task);

        var key = NormalizeSourcePath(item.FullPath);
        var stale = Entities.Where(e => string.Equals(
            NormalizeSourcePath(e.SourceFilePath), key, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var entity in stale) { Entities.Remove(entity); FilteredEntities.Remove(entity); }

        DrawingFiles.Remove(item);
        DetachDrawingFileObserver(item);
        for (var i = 0; i < DrawingFiles.Count; i++) DrawingFiles[i].Index = i + 1;

        if (SelectedDrawingFile == item)
            SelectedDrawingFile = DrawingFiles.Count == 0 ? null : DrawingFiles[Math.Min(index, DrawingFiles.Count - 1)];
        if (SelectedBatchTask == item) { SelectedBatchTask = null; IsTaskDetailOpen = false; }
        if (string.Equals(SelectedFilePath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            SelectedFilePath = SelectedDrawingFile?.FullPath ?? string.Empty;

        InvalidateEntityIndex();
        UpdateStatistics();
        ApplyFilter();
        RefreshBatch();
        HasDrawingFiles = DrawingFiles.Count > 0;
        HasMultipleDrawingFiles = DrawingFiles.Count > 1;
        UpdateDrawingSelection();
        RaiseWorkspaceSummaryProperties();
        RaiseBatchSummaryProperties();
        ScheduleWorkspaceSessionSave();
        StatusMessage = $"已把 {item.FileName} 移出队列（文件本身未删除）。";
    }

    partial void OnFilterStatusTextChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    private void FilterByStatus(string? status)
    {
        FilterStatusText = status ?? Strings.Get("FilterAll");
    }

    [RelayCommand]
    private void ApplySearch() => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<TextEntity> filtered = Entities;
        if (SelectedDrawingFile != null)
        {
            var selectedPath = SelectedDrawingFile.FullPath;
            filtered = filtered.Where(e => string.Equals(
                NormalizeSourcePath(e.SourceFilePath), selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        var filterAll = Strings.Get("FilterAll");
        if (FilterStatusText != filterAll)
        {
            var filterPending = Strings.Get("FilterPending");
            var filterTranslated = Strings.Get("FilterTranslated");
            var filterReviewed = Strings.Get("FilterReviewed");
            var filterFailed = Strings.Get("FilterFailed");
            var filterGlossaryHit = Strings.Get("FilterGlossaryHit");
            var filterSkipped = Strings.Get("FilterSkipped");

            if (FilterStatusText == filterPending)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Pending);
            else if (FilterStatusText == filterTranslated)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Translated);
            else if (FilterStatusText == filterReviewed)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Reviewed || e.Status == TranslationStatus.WritebackSuccess);
            else if (FilterStatusText == filterFailed)
                filtered = filtered.Where(e => e.Status == TranslationStatus.TranslationFailed);
            else if (FilterStatusText == filterGlossaryHit)
                filtered = filtered.Where(e => e.GlossaryHit);
            else if (FilterStatusText == filterSkipped)
                filtered = filtered.Where(e => e.Status == TranslationStatus.Skipped);
        }
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            filtered = filtered.Where(e =>
                (e.PlainText ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.TranslatedText ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.EntityType ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        FilteredEntities.Clear();
        foreach (var entity in filtered) FilteredEntities.Add(entity);
        VisibleCount = FilteredEntities.Count;
    }

    private void UpdateStatistics()
    {
        TotalCount = Entities.Count;
        TranslatedCount = Entities.Count(e => e.Status == TranslationStatus.Translated ||
                                                e.Status == TranslationStatus.Reviewed ||
                                                e.Status == TranslationStatus.WritebackSuccess);
        FailedCount = Entities.Count(e => e.Status == TranslationStatus.TranslationFailed);
        GlossaryHitCount = Entities.Count(e => e.GlossaryHit);
        CacheHitCount = _consistencyService.CacheSize;
        VisibleCount = FilteredEntities.Count;
        RefreshDrawingFileSummaries();
    }

    private readonly HashSet<DrawingFileItem> _observedDrawingFiles = [];

    private void DrawingFiles_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // ObservableCollection.Reset normally does not provide OldItems. Track the rows explicitly so
        // a clear/rebuild cannot leave stale PropertyChanged handlers attached to abandoned rows.
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            DetachDrawingFileObservers();
            foreach (var item in DrawingFiles) AttachDrawingFileObserver(item);
        }
        else
        {
            if (e.OldItems != null)
                foreach (DrawingFileItem item in e.OldItems) DetachDrawingFileObserver(item);
            if (e.NewItems != null)
                foreach (DrawingFileItem item in e.NewItems) AttachDrawingFileObserver(item);
        }

        HasDrawingFiles = DrawingFiles.Count > 0;
        HasMultipleDrawingFiles = DrawingFiles.Count > 1;
        if (SelectedDrawingFile != null && !DrawingFiles.Contains(SelectedDrawingFile))
            SelectedDrawingFile = null;
        if (SelectedBatchTask != null && !DrawingFiles.Contains(SelectedBatchTask))
        {
            SelectedBatchTask = null;
            IsTaskDetailOpen = false;
            IsProofreading = false;
        }

        UpdateDrawingSelection();
        RaiseWorkspaceSummaryProperties();
        RaiseBatchSummaryProperties();

        // 工作区变了就刷新"上次工作区"记录（延迟合并写盘），下次启动据此恢复。
        ScheduleWorkspaceSessionSave();
    }

    private void AttachDrawingFileObserver(DrawingFileItem item)
    {
        if (_observedDrawingFiles.Add(item))
            item.PropertyChanged += DrawingFileItem_PropertyChanged;
    }

    private void DetachDrawingFileObserver(DrawingFileItem item)
    {
        if (_observedDrawingFiles.Remove(item))
            item.PropertyChanged -= DrawingFileItem_PropertyChanged;
    }

    private void DetachDrawingFileObservers()
    {
        foreach (var item in _observedDrawingFiles)
            item.PropertyChanged -= DrawingFileItem_PropertyChanged;
        _observedDrawingFiles.Clear();
    }

    private void DrawingFileItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DrawingFileItem.IsIncludedForExport))
            UpdateDrawingSelection();

        RaiseWorkspaceSummaryProperties();
        RaiseBatchSummaryProperties();
    }
    partial void OnIsAllDrawingsSelectedChanged(bool value)
    {
        if (_updatingDrawingSelection) return;
        try
        {
            _updatingDrawingSelection = true;
            foreach (var item in DrawingFiles) item.IsIncludedForExport = value;
        }
        finally { _updatingDrawingSelection = false; }
        OnPropertyChanged(nameof(IsAllDrawingsSelected));
    }

    public void UpdateDrawingSelection()
    {
        if (_updatingDrawingSelection) return;
        var selected = DrawingFiles.Count > 0 && DrawingFiles.All(f => f.IsIncludedForExport);
        if (IsAllDrawingsSelected != selected)
        {
            _updatingDrawingSelection = true;
            IsAllDrawingsSelected = selected;
            _updatingDrawingSelection = false;
        }
        OnPropertyChanged(nameof(IsAllDrawingsSelected));
    }
    partial void OnSelectedDrawingFileChanged(DrawingFileItem? value)
    {
        SelectedFilePath = value == null
            ? (DrawingFiles.Count > 1 ? "全部文件" : DrawingFiles.FirstOrDefault()?.FileName ?? string.Empty)
            : value.FileName;
        ApplyFilter();
    }

    [RelayCommand]
    private void ShowAllDrawingFiles()
    {
        SelectedDrawingFile = null;
        // Refresh the one-way ListBox binding even if "all" was already the active filter and
        // the user only single-clicked a visual list item.
        OnPropertyChanged(nameof(SelectedDrawingFile));
        SelectedFilePath = DrawingFiles.Count > 1 ? "全部文件" : DrawingFiles.FirstOrDefault()?.FileName ?? string.Empty;
        ApplyFilter();
    }

    public void OpenDrawingFile(DrawingFileItem item)
    {
        if (DrawingFiles.Contains(item))
            SelectedDrawingFile = item;
    }

    public void MarkTranslationEdited(TextEntity entity)
    {
        if (!Entities.Contains(entity)) return;
        entity.Status = string.IsNullOrWhiteSpace(entity.TranslatedText)
            ? TranslationStatus.Pending
            : TranslationStatus.Reviewed;
        UpdateStatistics();
        ApplyFilter();
        StatusMessage = $"已保存 {entity.Handle} 的人工译文";
    }

    private void RebuildDrawingFileList(IEnumerable<string> sourceFiles,
        IReadOnlyDictionary<string, string>? importErrors = null)
    {
        DrawingFiles.Clear();
        foreach (var source in sourceFiles
                     .Select(NormalizeSourcePath)
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? error = null;
            if (importErrors != null)
            {
                var match = importErrors.FirstOrDefault(pair => string.Equals(
                    NormalizeSourcePath(pair.Key), source, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Key)) error = match.Value;
            }
            var item = new DrawingFileItem(source, error);
            item.Refresh(Entities);
            DrawingFiles.Add(item);
        }

        HasMultipleDrawingFiles = DrawingFiles.Count > 1;
        SelectedDrawingFile = DrawingFiles.Count == 1 ? DrawingFiles[0] : null;
        SelectedFilePath = DrawingFiles.Count > 1
            ? "全部文件"
            : DrawingFiles.FirstOrDefault()?.FileName ?? string.Empty;

        // 导入即入队后，新行要立刻挂上任务对象，任务表才有真实的状态列。
        HasDrawingFiles = DrawingFiles.Count > 0;
        AttachTasksToRows();
    }

    private void RefreshDrawingFileSummaries()
    {
        // Bucket the entities once. The previous shape re-scanned the whole entity list for every row,
        // so a workspace of D drawings and E entities cost D × E comparisons on each refresh.
        var bySource = new Dictionary<string, List<TextEntity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in Entities)
        {
            var key = NormalizeSourcePath(entity.SourceFilePath);
            if (!bySource.TryGetValue(key, out var bucket)) bySource[key] = bucket = new List<TextEntity>();
            bucket.Add(entity);
        }
        for (int i = 0; i < DrawingFiles.Count; i++)
        {
            var row = DrawingFiles[i];
            row.Index = i + 1;
            row.Refresh(bySource.TryGetValue(NormalizeSourcePath(row.FullPath), out var mine)
                ? mine
                : (IEnumerable<TextEntity>)Array.Empty<TextEntity>());
        }
        HasDrawingFiles = DrawingFiles.Count > 0;
    }

    #endregion
}

// Batch export selection helpers.



