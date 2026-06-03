using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using Microsoft.Win32;
using System.IO;
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
    private async Task ToggleLanguageDirectionAsync()
    {
        if (IsProcessing) return;

        var translatedCount = Entities.Count(e =>
            e.Status == TranslationStatus.Translated ||
            e.Status == TranslationStatus.Reviewed ||
            e.Status == TranslationStatus.WritebackSuccess);
        if (translatedCount > 0)
        {
            var confirmResult = MessageBox.Show(
                Strings.Get("MsgDirectionSwitchConfirm", translatedCount),
                Strings.Get("MsgTitleConfirm"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmResult != MessageBoxResult.Yes) return;
        }

        IsCnToEn = !IsCnToEn;

        if (IsCnToEn)
        {
            LanguageDirection = Strings.Get("LangCnToEn");
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
            _config.SourceLanguage = "ZH";
            _config.TargetLanguage = "EN";
        }
        else
        {
            LanguageDirection = Strings.Get("LangEnToCn");
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
            _config.SourceLanguage = "EN";
            _config.TargetLanguage = "ZH";
        }

        await RefreshGlossaryDataAsync();

        foreach (var entity in Entities)
        {
            if (entity.Status == TranslationStatus.Translated ||
                entity.Status == TranslationStatus.Reviewed ||
                entity.Status == TranslationStatus.WritebackSuccess)
            {
                entity.Status = TranslationStatus.Pending;
                entity.TranslatedText = string.Empty;
                entity.GlossaryHit = false;
            }
        }

        ApplyFilter();
        UpdateStatistics();
        StatusMessage = Strings.Get("StatusDirectionSwitched", LanguageDirection);
    }

    #endregion

    #region Glossary Management

    [RelayCommand]
    private void OpenGlossaryManager()
    {
        var titleSuffix = $"{LanguageDirection} ({GlossaryEntries.Count})";
        var dialog = new Views.GlossaryManagerDialog(_glossaryService, GlossaryEntries, titleSuffix)
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

        if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(dialog.FileName, targetPath, overwrite: true);
        }

        await _glossaryService.LoadGlossaryAsync(targetPath);
        RefreshGlossaryData();
        StatusMessage = Strings.Get("StatusGlossaryImported", GlossaryEntries.Count);
    }

    #endregion

    #region Settings & Help

    [RelayCommand]
    private void ToggleLogViewer()
    {
        LogViewModel.ToggleVisibilityCommand.Execute(null);
    }

    [RelayCommand]
    private void Settings()
    {
        if (IsProcessing) return;

        var dialog = new Views.SettingsDialog
        {
            Owner = Application.Current.MainWindow
        };
        var result = dialog.ShowDialog();

        if (result == true)
        {
            var oldApiKey = _config.DeepSeekApiKey;
            var oldBaseUrl = _config.DeepSeekBaseUrl;
            var oldModel = _config.DeepSeekModel;

            LoadConfig();

            if (_config.DeepSeekApiKey != oldApiKey ||
                _config.DeepSeekBaseUrl != oldBaseUrl ||
                _config.DeepSeekModel != oldModel)
            {
                _httpClient?.Dispose();
                _httpClient = null;
                _deepSeekClient = null;
                StatusMessage = Strings.Get("StatusApiReset");
            }
            else
            {
                StatusMessage = Strings.Get("StatusSettingsSaved");
            }
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
