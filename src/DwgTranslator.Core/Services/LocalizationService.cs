using System.Globalization;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Default implementation of <see cref="ILocalizationService"/>.
/// Uses .resx resource files via the strongly-typed <see cref="Strings"/> accessor.
/// </summary>
public class LocalizationService : ILocalizationService
{
    private string _currentLanguage = "zh-CN";

    private static readonly List<LanguageInfo> SupportedLanguages = new()
    {
        new LanguageInfo { CultureName = "zh-CN", DisplayName = "Chinese (Simplified)", NativeName = "简体中文" },
        new LanguageInfo { CultureName = "en-US", DisplayName = "English", NativeName = "English" }
    };

    public string CurrentLanguage => _currentLanguage;

    public IReadOnlyList<LanguageInfo> AvailableLanguages => SupportedLanguages;

    public event EventHandler? LanguageChanged;

    public LocalizationService()
    {
    }

    /// <summary>
    /// Initialize with a persisted language preference (from AppConfig).
    /// </summary>
    public LocalizationService(string? persistedLanguage)
    {
        if (!string.IsNullOrEmpty(persistedLanguage) &&
            SupportedLanguages.Any(l => l.CultureName == persistedLanguage))
        {
            SetLanguageInternal(persistedLanguage);
        }
    }

    public string GetString(string key)
    {
        return Strings.Get(key);
    }

    public string GetString(string key, params object[] args)
    {
        return Strings.Get(key, args);
    }

    public void SetLanguage(string cultureName)
    {
        if (_currentLanguage == cultureName) return;

        if (!SupportedLanguages.Any(l => l.CultureName == cultureName))
        {
            throw new ArgumentException($"Unsupported language: {cultureName}");
        }

        SetLanguageInternal(cultureName);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLanguageInternal(string cultureName)
    {
        _currentLanguage = cultureName;
        Strings.CurrentCulture = new CultureInfo(cultureName);
    }
}
