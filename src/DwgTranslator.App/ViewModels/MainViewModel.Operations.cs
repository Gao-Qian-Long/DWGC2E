using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Operations

    [RelayCommand]
    private void MarkAllReviewed()
    {
        if (IsProcessing) return;

        int count = 0;
        foreach (var entity in Entities.Where(e => e.Status == TranslationStatus.Translated))
        { entity.Status = TranslationStatus.Reviewed; count++; }
        UpdateStatistics();
        ApplyFilter();
        StatusMessage = Strings.Get("StatusReviewed", count);
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (IsProcessing) return;
        if (Entities.Count == 0) return;

        // 危险操作走主题化确认框（§29）
        var confirmed = Views.ConfirmDialog.Ask(
            Application.Current?.MainWindow,
            Strings.Get("MsgClearConfirmTitle"),
            Strings.Get("MsgClearConfirmBody", Entities.Count),
            confirmText: "清空", danger: true);
        if (!confirmed) return;

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

    private void DrawingFiles_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (DrawingFileItem item in e.OldItems) item.PropertyChanged -= DrawingFileItem_PropertyChanged;
        if (e.NewItems != null)
            foreach (DrawingFileItem item in e.NewItems) item.PropertyChanged += DrawingFileItem_PropertyChanged;
        UpdateDrawingSelection();
    }

    private void DrawingFileItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DrawingFileItem.IsIncludedForExport)) UpdateDrawingSelection();
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
        for (int i = 0; i < DrawingFiles.Count; i++)
        {
            DrawingFiles[i].Index = i + 1;
            DrawingFiles[i].Refresh(Entities);
        }
        HasDrawingFiles = DrawingFiles.Count > 0;
    }

    #endregion
}

// Batch export selection helpers.



