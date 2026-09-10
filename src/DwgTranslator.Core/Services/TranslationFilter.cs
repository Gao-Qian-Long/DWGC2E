using System.Text.RegularExpressions;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Text filtering utilities for the translation pipeline.
/// Determines which texts should be skipped from translation.
/// </summary>
internal static class TranslationFilter
{
    // Numeric / symbol-only text (dimensions, codes, pure numbers). Includes common
    // engineering symbols and Chinese numerals that should not be translated alone.
    private static readonly Regex NumericOnlyRegex = new(
        @"^[\s\d\.\,\+\-\*\/\=\<\>\(\)\[\]\{\|\\_~`@&\$\^±×÷°\#\%‰∞≈≠≤≥μΩΦφØø⌀′″'""·•–—]+$",
        RegexOptions.Compiled);

    // Common engineering tokens that should never be sent for translation alone
    // (unit/size/code labels that appear on mechanical drawings).
    private static readonly HashSet<string> EngineeringTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "KM", "CB", "M", "DN", "PN", "IP", "AC", "DC", "VAC", "VDC", "HZ", "RPM",
        "NPT", "BSP", "UNC", "UNF", "ISO", "DIN", "GB", "JB", "HRC", "HB", "HV",
        "Ra", "Rz", "A", "B", "C", "D", "E", "F", "G", "H", "L", "R", "S", "T",
        "W", "X", "Y", "Z", "N", "P", "Q", "V", "PE", "PEN", "GND", "NC", "NO",
        "RFS", "MMC", "LMC"
    };

    /// <summary>
    /// Check if text should be skipped from translation (numeric-only, already target language, etc.).
    /// </summary>
    public static bool ShouldSkipTranslation(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var trimmed = text.Trim();

        // Pure engineering token (KM, CB, M8 already handled by length/digit rule below)
        if (IsEngineeringToken(trimmed)) return true;

        // Engineering labels: short text with digits and few letters (e.g. 24V, φ12, M8, IP65, 50Hz)
        // Do not classify natural labels such as "Motor 2" as engineering codes
        // merely because they contain a digit and only a few letters.

        // Short all-caps alphanumeric codes without CJK (e.g. KM, CB, GB/T, H7)
        if (!HasCjk(trimmed) && trimmed.Length <= 15 && IsMostlyCode(trimmed))
            return true;

        // Already-target-language detection. The source language's script has to actually appear,
        // otherwise the text is already written in the target language. Pairs that share a script
        // (Chinese to Japanese) cannot be told apart this way, so they use the target language's
        // distinguishing script instead (kana, hangul).
        if (!string.Equals(sourceLang, targetLang, StringComparison.OrdinalIgnoreCase))
        {
            bool sourceIsCjk = DwgTranslator.Core.Models.TranslationLanguages.IsCjk(sourceLang);
            bool targetIsCjk = DwgTranslator.Core.Models.TranslationLanguages.IsCjk(targetLang);
            if (sourceIsCjk && !targetIsCjk)
            {
                if (!HasCjk(trimmed)) return true;          // e.g. Chinese/Japanese into English
            }
            else if (!sourceIsCjk && targetIsCjk)
            {
                if (!HasAsciiLetters(trimmed)) return true; // e.g. English into Chinese/Japanese
            }
            else if (sourceIsCjk && targetIsCjk && IsDistinctiveScript(trimmed, targetLang))
            {
                return true;                                // e.g. kana already present when going to Japanese
            }
        }

        return false;
    }

    /// <summary>True when the text already carries the target language's distinguishing script.</summary>
    private static bool IsDistinctiveScript(string text, string targetLang)
    {
        var language = DwgTranslator.Core.Models.TranslationLanguages.Normalize(targetLang);
        return language switch
        {
            "JA" => text.Any(c => (c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF')),
            "KO" => text.Any(c => (c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF')),
            _ => false
        };
    }

    public static bool IsNumericOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        return NumericOnlyRegex.IsMatch(text.Trim());
    }

    public static bool HasCjk(string text) => TranslationQualityValidator.ContainsCjk(text);
    public static bool HasAsciiLetters(string text) => text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    private static bool IsEngineeringToken(string text)
    {
        if (EngineeringTokens.Contains(text)) return true;

        // Patterns like M8, M10x1.5, IP65, DN50, PN16, R3.2, 2xM8
        if (Regex.IsMatch(text, @"^(?:[0-9]+[xX×])?(?:M|DN|PN|IP|R|G|NPT|UNC|UNF)\d+(?:[.xX×/]\d+)*$", RegexOptions.IgnoreCase))
            return true;

        // Unit-like: 24V, 50Hz, 3kW, 10mm, 0.5MPa
        if (Regex.IsMatch(text, @"^\d+(\.\d+)?\s*(V|VAC|VDC|A|mA|kW|W|Hz|rpm|mm|cm|m|kg|N|MPa|kPa|bar|°C|C)?$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static bool IsMostlyCode(string text)
    {
        // All caps letters/digits with optional / - . (e.g. GB/T, H7, CB)
        int letters = 0, others = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                if (char.IsLower(c)) return false;
                letters++;
            }
            else if (char.IsDigit(c) || c is '/' or '-' or '.' or '_')
            {
                // allowed separators
            }
            else if (!char.IsWhiteSpace(c))
            {
                others++;
            }
        }
        // Pure alphabetic uppercase words (MOTOR, VALVE, OPEN...) are language,
        // not identifiers. Require a digit or an explicit code separator.
        bool hasCodeMarker = text.Any(char.IsDigit) && !text.Any(char.IsWhiteSpace);
        return letters > 0 && others == 0 && hasCodeMarker;
    }

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
        // Strip surrounding markdown code fences some models emit
        if (text.StartsWith("```") && text.EndsWith("```"))
        {
            text = text.Trim('`').Trim();
            if (text.StartsWith("text", StringComparison.OrdinalIgnoreCase))
                text = text[4..].TrimStart();
        }
        return text;
    }
}
