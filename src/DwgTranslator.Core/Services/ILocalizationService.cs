using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Provides localized string lookup and runtime language switching.
/// Backed by .resx resource files (Strings.resx for zh-CN, Strings.en-US.resx for en-US).
/// </summary>
public interface ILocalizationService
{
    /// <summary>Current UI culture code, e.g. "zh-CN" or "en-US".</summary>
    string CurrentLanguage { get; }

    /// <summary>Get a localized string by resource key.</summary>
    string GetString(string key);

    /// <summary>Get a localized string by resource key with format arguments.</summary>
    string GetString(string key, params object[] args);

    /// <summary>Switch the UI language at runtime. Persists the choice to AppConfig.</summary>
    void SetLanguage(string cultureName);

    /// <summary>Raised after the language has been changed. UI should rebind/reload.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>List of all supported languages.</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }
}
