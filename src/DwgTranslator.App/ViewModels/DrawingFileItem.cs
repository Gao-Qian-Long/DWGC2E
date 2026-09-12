using CommunityToolkit.Mvvm.ComponentModel;
using DwgTranslator.Core.Models;
using System.IO;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// One imported drawing in the multi-file workspace. The checkbox controls whether the drawing
/// participates in the next batch export; opening the item only changes the rows being edited.
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

    [ObservableProperty] private bool _isIncludedForExport = true;
    [ObservableProperty] private int _entityCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;

    public string SummaryText => string.IsNullOrWhiteSpace(ImportError)
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
        OnPropertyChanged(nameof(SummaryText));
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
