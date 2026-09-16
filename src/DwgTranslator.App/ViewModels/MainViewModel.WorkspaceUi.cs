using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows;
using System.IO;
using System.Diagnostics;
using DwgTranslator.Core.Tasks;

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
    public string ShellStatusText => IsProcessing ? StatusMessage : "就绪";
    partial void OnIsProcessingChanged(bool value) => OnPropertyChanged(nameof(ShellStatusText));
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(ShellStatusText));
    public bool HasFailedDrawingTasks => DrawingFiles.Any(IsFailedTask);
    public string BatchCountText => $"全部 {DrawingFiles.Count}    运行中 {DrawingFiles.Count(x => x.IsActive)}    已完成 {DrawingFiles.Count(x => x.Task?.Status == TranslationTaskStatus.Completed)}    失败 {DrawingFiles.Count(IsFailedTask)}";
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
            OnPropertyChanged(nameof(BatchCountText)); OnPropertyChanged(nameof(HasFailedDrawingTasks));
        };
        foreach (var row in DrawingFiles) { _batchRows.Add(row); PropertyChangedEventManager.AddHandler(row, BatchRowChanged, ""); }
        return view;
    }
    private bool _batchRefreshQueued;
    private void BatchRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DrawingFileItem.StatusText) || _batchRefreshQueued) return;
        _batchRefreshQueued = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => { _batchRefreshQueued = false; RefreshBatch(); }));
    }
    private static bool IsFailedTask(DrawingFileItem row) => row.HasError || row.Task?.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted;
    private bool MatchBatch(object value)
    {
        if (value is not DrawingFileItem row) return false;
        if (!row.FileName.Contains((BatchSearch ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (BatchDateFilter > 0 && (row.Task == null || row.Task.CreatedAt.ToLocalTime().Date < DateTime.Today.AddDays(BatchDateFilter == 1 ? 0 : -6))) return false;
        return BatchStatusFilter switch { 1 => row.IsActive, 2 => row.Task?.Status == TranslationTaskStatus.Pending || row.Task == null && !row.IsActive && !row.IsFinished && !row.HasError, 3 => row.Task?.Status == TranslationTaskStatus.Completed || row.IsFinished && !row.HasError && row.Task == null, 4 => IsFailedTask(row), 5 => row.Task?.Status == TranslationTaskStatus.Paused, 6 => row.Task?.Status == TranslationTaskStatus.Cancelled, 7 => row.Task?.Status == TranslationTaskStatus.Skipped, _ => true };
    }
    private void RefreshBatch() { _batchView?.Refresh(); OnPropertyChanged(nameof(BatchCountText)); OnPropertyChanged(nameof(HasFailedDrawingTasks)); }
    partial void OnBatchSearchChanged(string value) => RefreshBatch();
    partial void OnBatchStatusFilterChanged(int value) => RefreshBatch();
    partial void OnBatchDateFilterChanged(int value) => RefreshBatch();
    partial void OnSelectedBatchTaskChanged(DrawingFileItem? value) { if (value != null) IsTaskDetailOpen = true; }
    [RelayCommand] private void SelectAllDrawingOutputs() { foreach (var row in DrawingFiles) row.IsIncludedForExport = true; }
    [RelayCommand] private void ClearDrawingOutputs() { foreach (var row in DrawingFiles) row.IsIncludedForExport = false; }
    [RelayCommand] private void ShowTaskDetail(DrawingFileItem? row) { if (row == null) return; SelectedBatchTask = row; IsTaskDetailOpen = true; }
    [RelayCommand] private void CloseTaskDetail() => IsTaskDetailOpen = false;
    [RelayCommand] private void OpenTaskProofreading() { if (SelectedBatchTask == null || !ConfirmLeaveProofreading()) return; SelectedDrawingFile = SelectedBatchTask; ApplyFilter(); IsProofreading = true; IsTaskDetailOpen = false; }
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
        DiscardProofreading();
        return true;
    }
    [RelayCommand]
    private void OpenDrawingOutput(DrawingFileItem? row)
    {
        var path = row?.Task?.OutputPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { StatusMessage = "输出文件尚未生成或已移动，请先导出图纸。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { StatusMessage = "无法打开输出文件，请确认已安装对应的 CAD 软件，或从输出目录打开。"; }
    }
}
