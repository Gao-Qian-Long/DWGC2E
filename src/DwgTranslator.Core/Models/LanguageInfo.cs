namespace DwgTranslator.Core.Models;

/// <summary>
/// Represents a supported UI language.
/// </summary>
public class LanguageInfo
{
    /// <summary>Culture code, e.g. "zh-CN", "en-US".</summary>
    public string CultureName { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI, e.g. "Chinese (Simplified)", "English".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Native display name, e.g. "简体中文", "English".</summary>
    public string NativeName { get; set; } = string.Empty;

    public override string ToString() => NativeName;
}
