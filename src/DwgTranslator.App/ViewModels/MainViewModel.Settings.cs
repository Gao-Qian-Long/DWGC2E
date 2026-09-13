using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using ClosedXML.Excel;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Language Direction Toggle

    [RelayCommand]
    private void ToggleTopmost()
    {
        if (Application.Current.MainWindow != null)
        {
            Application.Current.MainWindow.Topmost = !Application.Current.MainWindow.Topmost;
            IsTopmost = Application.Current.MainWindow.Topmost;
        }
        StatusMessage = IsTopmost ? Strings.Get("StatusTopmostOn") : Strings.Get("StatusTopmostOff");
    }

    [RelayCommand]
    private void SwapLanguages()
    {
        if (IsProcessing) return;
        if (!ConfirmDiscardTranslations()) return;

        // One assignment would fire the change handler twice, so the swap is applied in one step.
        _applyingLanguagePair = true;
        (CurrentSourceLang, CurrentTargetLang) = (CurrentTargetLang, CurrentSourceLang);
        _applyingLanguagePair = false;
        LanguagePairChanged();
    }

    /// <summary>
    /// Opens the language chooser. Any language may be translated into any other, so this dialog is
    /// the only place the direction is decided; the title bar shows the current pair and reopens it.
    /// </summary>
    [RelayCommand]
    private void ChooseLanguages()
    {
        if (IsProcessing) return;

        CurrentPage = PageTranslate;
    }

    /// <summary>
    /// Asks before dropping translations that belong to the previous language pair.
    /// </summary>
    private bool ConfirmDiscardTranslations()
    {
        var translatedCount = Entities.Count(e =>
            e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess);
        if (translatedCount == 0) return true;

        return DwgTranslator.App.Views.PromptDialog.Show(
            Strings.Get("MsgDirectionSwitchConfirm", translatedCount),
            Strings.Get("MsgTitleConfirm"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    #endregion

    #region Glossary Management

    [RelayCommand]
    private void OpenGlossaryManager() { CurrentPage = PageGlossary; LoadTermEditor(); }

    private void RefreshGlossaryDataFromList(List<GlossaryEntry> entries)
    {
        GlossaryEntries.Clear();
        foreach (var entry in entries)
            GlossaryEntries.Add(entry);
        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    [RelayCommand]
    private async Task ImportGlossaryAsync()
    {
        if (IsProcessing || !ConfirmLeaveGlossary()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "术语库文件|*.json;*.xlsx|JSON|*.json|Excel|*.xlsx",
            Title = Strings.Get("DialogTitleImportGlossary")
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            List<GlossaryEntry> entries;
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                entries = JsonSerializer.Deserialize<List<GlossaryEntry>>(await File.ReadAllTextAsync(dialog.FileName), AppConfigJson.ReadOptions) ?? throw new InvalidDataException();
            else if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                entries = await Task.Run(() => ReadGlossaryExcel(dialog.FileName));
            else throw new InvalidDataException();
            if (entries.Count > 1000 || entries.Any(t => t == null || string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target))) throw new InvalidDataException();
            CurrentPage = PageGlossary;
            if (CurrentPage != PageGlossary) return;
            LoadTermEditor();
            TermDraft.Clear();
            foreach (var entry in entries) TermDraft.Add(entry);
            SelectedTerm = null;
            RefreshTermView();
            TermFeedback = "已导入到草稿，请检查冲突并保存。原术语文件尚未更改。";
        }
        catch
        {
            TermFeedback = "导入失败：请选择有效的 JSON 或 XLSX 文件（原文、译文不能为空，最多 1000 条）。原术语未更改。";
            StatusMessage = TermFeedback;
        }
    }

    private static List<GlossaryEntry> ReadGlossaryExcel(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidDataException("Excel文件中没有工作表。");
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
        var entries = new List<GlossaryEntry>();

        for (var row = 1; row <= lastRow; row++)
        {
            var source = worksheet.Cell(row, 1).GetString().Trim();
            var target = worksheet.Cell(row, 2).GetString().Trim();
            var category = worksheet.Cell(row, 3).GetString().Trim();
            if (row == 1 &&
                (source.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                 source is "原文" or "源语言" or "中文"))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;
            entries.Add(new GlossaryEntry { Source = source, Target = target, Category = category });
        }

        return entries;
    }

    #endregion

    #region Settings & Help

    [RelayCommand]
    private void ToggleLogViewer()
    {
        LogViewModel.ToggleVisibilityCommand.Execute(null);
    }

    // 保留命令符号以兼容旧绑定，但不再打开本地高级设置窗口。
    [RelayCommand]
    private void Settings() => CurrentPage = PageSettings;

    [RelayCommand]
    private void ShowHelp() { SettingsSection = 4; CurrentPage = PageSettings; }

    #endregion

    #region License

    [RelayCommand]
    private void OpenLicenseDialog()
    {
        var dialog = new Views.LicenseDialog(_licenseService)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
        RefreshLicenseStatus();
    }

    private void PromptForActivation()
    {
        var result = DwgTranslator.App.Views.PromptDialog.Show(
            Strings.Get("MsgActivationPrompt"),
            Strings.Get("MsgTitleActivation"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            OpenLicenseDialog();
        }
    }

    private bool ConsumeLicenseForExport()
    {
        if (!_config.LicensingEnabled)
            return true;

        if (!_licenseService.CanExecuteOperation())
        {
            PromptForActivation();
            return false;
        }

        if (_licenseService.CurrentLicense.Type == LicenseType.Trial)
        {
            _licenseService.ConsumeTrialUse();
            RefreshLicenseStatus();
        }
        return true;
    }

    #endregion
}





