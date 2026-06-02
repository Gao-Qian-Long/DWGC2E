using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using System.IO;
using System.Text.Json;
using Serilog;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    #region Config

    private void LoadConfig()
    {
        try
        {
            var appDataPath = Path.Combine(App.AppDataDir, "settings.json");
            var bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            _settingsPath = File.Exists(appDataPath) ? appDataPath : bundledPath;

            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }

            _config.DeepSeekApiKey = AppConfig.DecryptApiKey(_config.DeepSeekApiKey);

            if (!Path.IsPathRooted(_config.ExportDirectory))
                _config.ExportDirectory = Path.Combine(App.AppDataDir, _config.ExportDirectory);
            if (!Path.IsPathRooted(_config.LogDirectory))
                _config.LogDirectory = Path.Combine(App.AppDataDir, _config.LogDirectory);
            if (!Path.IsPathRooted(_config.GlossaryPath))
            {
                var appDataGlossary = Path.Combine(App.AppDataDir, _config.GlossaryPath);
                if (File.Exists(appDataGlossary))
                    _config.GlossaryPath = appDataGlossary;
            }

            Directory.CreateDirectory(_config.ExportDirectory);

            RefreshGlossaryData();

            _consistencyService?.FlushCache();
            var cachePath = Path.Combine(App.AppDataDir, "translation_cache.json");
            _consistencyService = new TranslationConsistencyService(cachePath);

            ApplyLanguageDirection();
            StatusMessage = Strings.Get("StatusReadyWithGlossary", GlossaryEntries.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Config load error");
            StatusMessage = Strings.Get("StatusConfigLoadFailed");
        }
    }

    private void ApplyLanguageDirection()
    {
        if (_config.SourceLanguage == "ZH" && _config.TargetLanguage == "EN")
        {
            IsCnToEn = true;
            LanguageDirection = Strings.Get("LangCnToEn");
            CurrentSourceLang = "ZH";
            CurrentTargetLang = "EN";
        }
        else
        {
            IsCnToEn = false;
            LanguageDirection = Strings.Get("LangEnToCn");
            CurrentSourceLang = "EN";
            CurrentTargetLang = "ZH";
        }
    }

    private void RefreshGlossaryData()
    {
        GlossaryEntries.Clear();
        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);
        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    private async Task RefreshGlossaryDataAsync()
    {
        GlossaryEntries.Clear();

        var glossaryFile = ResolveGlossaryPath();
        if (File.Exists(glossaryFile))
        {
            await _glossaryService.LoadGlossaryAsync(glossaryFile);
        }

        foreach (var entry in _glossaryService.GetAllEntries())
            GlossaryEntries.Add(entry);

        GlossaryStatsText = Strings.Get("StatsGlossaryCount", GlossaryEntries.Count);
    }

    private string ResolveGlossaryPath()
    {
        var appDataGlossary = Path.Combine(App.AppDataDir, "glossaries", "mechanical_zh_en.json");
        if (File.Exists(appDataGlossary)) return appDataGlossary;

        var bundledGlossary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "glossaries", "mechanical_zh_en.json");
        if (File.Exists(bundledGlossary)) return bundledGlossary;

        return _config.GlossaryPath;
    }

    #endregion
}
