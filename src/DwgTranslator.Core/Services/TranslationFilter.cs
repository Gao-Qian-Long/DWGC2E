using System.Text.RegularExpressions;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Text filtering utilities for the translation pipeline.
/// Determines which texts should be skipped from translation.
/// </summary>
internal static class TranslationFilter
{
    private static readonly Regex NumericOnlyRegex = new(
        @"^[\s\d\.\,\+\-\*\/\=<>≤≥±°\#\%‰〇零一二三四五六七八九十百千万亿φΦ⌀ⓧⓓ]+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Check if text should be skipped from translation (numeric-only, already target language, etc.).
    /// </summary>
    public static bool ShouldSkipTranslation(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var trimmed = text.Trim();

        // Engineering labels: short text with digits and few letters (e.g. 24V, Φ12, M8, IP65, 50Hz)
        if (trimmed.Length <= 15 && trimmed.Any(char.IsDigit) && !HasCjk(trimmed))
        {
            var letterCount = trimmed.Count(char.IsLetter);
            if (letterCount <= 5) return true;
        }

        // Already target language detection
        if (sourceLang == "ZH" && targetLang == "EN")
        {
            if (!HasCjk(trimmed)) return true; // No CJK = already English
        }
        else if (sourceLang == "EN" && targetLang == "ZH")
        {
            if (!HasAsciiLetters(trimmed)) return true; // No ASCII letters = already Chinese
        }

        return false;
    }

    public static bool IsNumericOnly(string text) => NumericOnlyRegex.IsMatch(text.Trim());
    public static bool HasCjk(string text) => text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
    public static bool HasAsciiLetters(string text) => text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    /// <summary>
    /// Clean translation output: trim, strip "Translated:" prefix.
    /// </summary>
    public static string CleanTranslationOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = text.Trim();
        if (text.StartsWith("Translated", StringComparison.OrdinalIgnoreCase) && text.Contains(":"))
        {
            var ci = text.IndexOf(':');
            if (ci > 0 && ci < 30) text = text[(ci + 1)..].Trim();
        }
        return text;
    }
}
