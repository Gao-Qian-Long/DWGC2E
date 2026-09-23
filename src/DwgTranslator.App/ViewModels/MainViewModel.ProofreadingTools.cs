using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _batchFindText = string.Empty;
    [ObservableProperty] private string _batchReplacementText = string.Empty;
    [ObservableProperty] private int _proofreadingScope; // 0 当前图纸，1 所选图纸，2 全项目
    [ObservableProperty] private int _batchReplaceMatchCount;
    private List<(TextEntity Entity, string Text, TranslationStatus Status, bool GlossaryHit, bool WasTrackedBefore)>? _lastBulkEdit;
    public bool CanUndoBulkEdit => _lastBulkEdit?.Count > 0;

    private IEnumerable<TextEntity> ProofreadingScopeEntities()
    {
        if (ProofreadingScope == 2) return Entities;
        if (ProofreadingScope == 1)
        {
            var selected = DrawingFiles.Where(d => d.IsIncludedForExport)
                .Select(d => NormalizeSourcePath(d.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Entities.Where(e => selected.Contains(NormalizeSourcePath(e.SourceFilePath)));
        }
        if (SelectedDrawingFile == null) return Entities;
        var path = NormalizeSourcePath(SelectedDrawingFile.FullPath);
        return Entities.Where(e => string.Equals(NormalizeSourcePath(e.SourceFilePath), path, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnBatchFindTextChanged(string value) => PreviewBatchReplace();
    partial void OnProofreadingScopeChanged(int value) => PreviewBatchReplace();

    [RelayCommand]
    private void PreviewBatchReplace()
    {
        var find = BatchFindText;
        BatchReplaceMatchCount = string.IsNullOrEmpty(find) ? 0 : ProofreadingScopeEntities().Count(e =>
            (e.TranslatedText ?? string.Empty).Contains(find, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void ApplyBatchReplace()
    {
        PreviewBatchReplace();
        if (BatchReplaceMatchCount == 0) { StatusMessage = "没有找到可替换的译文。"; return; }
        var sample = ProofreadingScopeEntities().Where(e => (e.TranslatedText ?? string.Empty).Contains(BatchFindText, StringComparison.OrdinalIgnoreCase))
            .Take(5).Select(e => $"{e.TranslatedText} → {(e.TranslatedText ?? string.Empty).Replace(BatchFindText, BatchReplacementText, StringComparison.OrdinalIgnoreCase)}");
        var answer = Views.PromptDialog.Show($"将修改 {BatchReplaceMatchCount} 条译文：\n\n{string.Join("\n", sample)}\n\n确认一次提交？",
            "批量替换预览", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        var targets = ProofreadingScopeEntities().Where(e => (e.TranslatedText ?? string.Empty).Contains(BatchFindText, StringComparison.OrdinalIgnoreCase)).ToArray();
        _lastBulkEdit = targets.Select(e => (e, e.TranslatedText ?? string.Empty, e.Status, e.GlossaryHit, _proofreadingOriginals.ContainsKey(e))).ToList();
        foreach (var entity in targets)
        {
            TrackProofreadingEdit(entity);
            entity.TranslatedText = (entity.TranslatedText ?? string.Empty).Replace(BatchFindText, BatchReplacementText, StringComparison.OrdinalIgnoreCase);
            entity.Status = TranslationStatus.Reviewed;
        }
        OnPropertyChanged(nameof(CanUndoBulkEdit)); ApplyFilter(); UpdateStatistics(); ScheduleProjectAutosave();
        StatusMessage = $"已批量替换 {targets.Length} 条译文，可撤销最近一次批量操作。";
    }

    [RelayCommand]
    private void UndoBulkEdit()
    {
        if (_lastBulkEdit == null) return;
        foreach (var item in _lastBulkEdit)
        {
            item.Entity.TranslatedText = item.Text;
            item.Entity.Status = item.Status;
            item.Entity.GlossaryHit = item.GlossaryHit;
            // 这次批量操作才首次建立的“未保存校对”快照，在完整撤销后也必须移除；
            // 如果操作前本来就有人工编辑，则保留原快照，离开页面时仍应提示保存。
            if (!item.WasTrackedBefore) _proofreadingOriginals.Remove(item.Entity);
        }
        _proofreadingWorkspaceVersion++;
        var count = _lastBulkEdit.Count; _lastBulkEdit = null;
        OnPropertyChanged(nameof(CanUndoBulkEdit));
        OnPropertyChanged(nameof(HasUnsavedProofreading));
        ApplyFilter(); UpdateStatistics(); ScheduleProjectAutosave(); StatusMessage = $"已撤销最近一次批量操作（{count} 条）。";
    }

    [RelayCommand]
    private void ApplyGlossaryToProject()
    {
        var terms = EffectiveGlossary.Resolve(GlossaryEntries, CurrentSourceLang, CurrentTargetLang);
        var changes = new List<(TextEntity Entity, string NewText)>();
        var sourceHits = 0;
        foreach (var entity in ProofreadingScopeEntities())
        {
            var revised = EffectiveGlossary.ApplyToExistingTranslation(entity.PlainText, entity.TranslatedText ?? string.Empty, terms, out var entityHits);
            sourceHits += entityHits;
            if (!string.Equals(revised, entity.TranslatedText, StringComparison.Ordinal)) changes.Add((entity, revised));
        }
        if (changes.Count == 0)
        {
            StatusMessage = sourceHits == 0 ? "当前范围没有命中新术语。" : $"命中 {sourceHits} 个术语，但现有译文无法安全自动映射；请手动校对或重新翻译。";
            return;
        }
        var preview = string.Join("\n", changes.Take(5).Select(c => $"{c.Entity.TranslatedText} → {c.NewText}"));
        if (Views.PromptDialog.Show($"术语预览：命中 {sourceHits} 个术语，可安全修改 {changes.Count} 条译文。无法安全映射的句子不会被修改。\n\n{preview}", "应用术语到当前项目", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _lastBulkEdit = changes.Select(c => (c.Entity, c.Entity.TranslatedText ?? string.Empty, c.Entity.Status, c.Entity.GlossaryHit, _proofreadingOriginals.ContainsKey(c.Entity))).ToList();
        foreach (var change in changes) { TrackProofreadingEdit(change.Entity); change.Entity.TranslatedText = change.NewText; change.Entity.Status = TranslationStatus.Reviewed; change.Entity.GlossaryHit = true; }
        OnPropertyChanged(nameof(CanUndoBulkEdit)); ApplyFilter(); UpdateStatistics(); ScheduleProjectAutosave();
    }
}
