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

        var dialog = new Views.LanguagePairDialog(CurrentSourceLang, CurrentTargetLang)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        if (string.Equals(dialog.SourceCode, CurrentSourceLang, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(dialog.TargetCode, CurrentTargetLang, StringComparison.OrdinalIgnoreCase))
            return;

        if (!ConfirmDiscardTranslations()) return;

        _applyingLanguagePair = true;
        CurrentSourceLang = dialog.SourceCode;
        CurrentTargetLang = dialog.TargetCode;
        _applyingLanguagePair = false;
        LanguagePairChanged();
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

        return MessageBox.Show(
            Strings.Get("MsgDirectionSwitchConfirm", translatedCount),
            Strings.Get("MsgTitleConfirm"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    #endregion

    #region Glossary Management

    [RelayCommand]
    private void OpenGlossaryManager()
    {
        var titleSuffix = $"{LanguageDirection} ({GlossaryEntries.Count})";
        var dialog = new Views.GlossaryManagerDialog(_glossaryService, GlossaryEntries, titleSuffix, _apiClient)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.SavedEntries != null)
        {
            RefreshGlossaryDataFromList(dialog.SavedEntries);
            StatusMessage = Strings.Get("StatusGlossarySaved", dialog.SavedEntries.Count);
        }
    }

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
        if (IsProcessing) return;

        var dialog = new OpenFileDialog
        {
            Filter = Strings.Get("FilterGlossaryFiles"),
            Title = Strings.Get("DialogTitleImportGlossary")
        };
        if (dialog.ShowDialog() != true) return;

        var targetPath = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(dialog.FileName, targetPath, overwrite: true);
            }
            else if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                var entries = await Task.Run(() => ReadGlossaryExcel(dialog.FileName));
                if (entries.Count == 0)
                    throw new InvalidDataException("Excel术语库中没有有效数据。请在前两列填写原文和译文，第三列可填写分类。");

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(targetPath, json);
            }
            else
            {
                throw new InvalidDataException("仅支持 JSON 或 XLSX 术语库文件。");
            }

            await _glossaryService.LoadGlossaryAsync(targetPath);
            RefreshGlossaryData();
            StatusMessage = Strings.Get("StatusGlossaryImported", GlossaryEntries.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"术语库导入失败：{ex.Message}";
            MessageBox.Show(StatusMessage, Strings.Get("MsgTitleError"),
                MessageBoxButton.OK, MessageBoxImage.Error);
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

        return entries
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
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
    private void Settings()
    {
        if (IsProcessing) return;
        var dialog = new Views.SettingsDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            LoadConfig();
            StatusMessage = "设置已保存";
        }
    }

    [RelayCommand]
    private void ShowHelp()
    {
        var dialog = new Views.HelpDialog
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

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
        var result = MessageBox.Show(
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





