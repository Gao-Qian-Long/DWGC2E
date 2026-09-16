using System.Globalization;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Default implementation of <see cref="ILocalizationService"/>.
/// Fixed to zh-CN only. Language switching is no longer supported.
/// </summary>
public class LocalizationService : ILocalizationService
{
    private const string FixedLanguage = "zh-CN";

    private static readonly List<LanguageInfo> SupportedLanguages = new()
    {
        new LanguageInfo { CultureName = "zh-CN", DisplayName = "Chinese (Simplified)", NativeName = "简体中文" }
    };

    public string CurrentLanguage => FixedLanguage;

    public IReadOnlyList<LanguageInfo> AvailableLanguages => SupportedLanguages;

    public event EventHandler? LanguageChanged
    {
        add { }
        remove { }
    }

    public LocalizationService()
    {
    }

    /// <summary>
    /// Initialize with a persisted language preference (ignored — only zh-CN is supported).
    /// </summary>
    public LocalizationService(string? persistedLanguage)
    {
        // Language switching is disabled; always use zh-CN.
    }

    public string GetString(string key)
    {
        return Strings.Get(key);
    }

    public string GetString(string key, params object[] args)
    {
        return Strings.Get(key, args);
    }

    [Obsolete("Language switching is no longer supported. UI is fixed to zh-CN.", false)]
    public void SetLanguage(string cultureName)
    {
        // No-op: language is fixed to zh-CN
    }

    private void SetLanguageInternal(string cultureName)
    {
        // No-op: language is fixed to zh-CN (Strings.CurrentCulture is now read-only)
    }
}
