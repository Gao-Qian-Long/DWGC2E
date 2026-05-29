using System.Text.RegularExpressions;

namespace DwgTranslator.Core.Translation;

/// <summary>
/// Parses and preserves MTEXT/DIMENSION format control codes during translation.
/// Format codes like \P, \f..., \A1;, %%c, etc. are replaced with placeholders before translation,
/// then restored afterward. Brace grouping characters {} are stripped but not treated as format codes.
/// </summary>
public partial class FormatCodeParser
{
    // Matches format code patterns: \fArial;, \C3;, \H2x;, \P, \U+XXXX, \~, \%, %%c, etc.
    // Does NOT match literal braces {} (stripped separately) to preserve text content inside brace groups.
    [GeneratedRegex(@"\\[A-Za-z][^;{}]*;|\\U\+[0-9A-Fa-f]{4}|\\[~%%|{}]|%%[cdpuoCDPUO]|\\[A-Za-z]")]
    private static partial Regex FormatCodeRegex();

    private const string PlaceholderPrefix = "__FMT_";
    private const string PlaceholderSuffix = "__";

    /// <summary>
    /// Extracts format codes from text and returns a template with placeholders.
    /// Uses position-based replacement to avoid corruption when codes share prefixes.
    /// </summary>
    /// <param name="rawText">The raw text containing format codes.</param>
    /// <returns>A tuple of (plainText, formatTemplate, formatCodes).</returns>
    public (string PlainText, string FormatTemplate, List<string> FormatCodes) Parse(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return (string.Empty, string.Empty, new List<string>());

        var regex = FormatCodeRegex();
        var matches = regex.Matches(rawText);

        if (matches.Count == 0)
        {
            var cleaned = StripBraces(rawText);
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
            return (cleaned, cleaned, new List<string>());
        }

        // Extract format codes and build position-based replacement map
        var formatCodes = new List<string>();
        var replacements = new List<(int Start, int Length, string Placeholder)>();

        for (int i = 0; i < matches.Count; i++)
        {
            formatCodes.Add(matches[i].Value);
            replacements.Add((matches[i].Index, matches[i].Length, $"{PlaceholderPrefix}{i}{PlaceholderSuffix}"));
        }

        // Replace from right to left to preserve positions
        var sb = new System.Text.StringBuilder(rawText);
        for (int i = replacements.Count - 1; i >= 0; i--)
        {
            var (start, length, placeholder) = replacements[i];
            sb.Remove(start, length);
            sb.Insert(start, placeholder);
        }

        // Keep template with placeholders for restore (preserve braces for format grouping)
        var template = sb.ToString();
        // Strip grouping braces and clean whitespace for plain text only
        var result = StripBraces(template);
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return (result, template, formatCodes);
    }

    /// <summary>
    /// Restores format codes in translated text using the stored format codes.
    /// </summary>
    /// <param name="translatedText">The translated text with placeholders.</param>
    /// <param name="formatCodes">The original format codes to restore.</param>
    /// <returns>The text with format codes restored.</returns>
    public string Restore(string translatedText, List<string> formatCodes)
    {
        if (string.IsNullOrEmpty(translatedText) || formatCodes == null || formatCodes.Count == 0)
            return translatedText;

        var result = translatedText;
        for (int i = 0; i < formatCodes.Count; i++)
        {
            var placeholder = $"{PlaceholderPrefix}{i}{PlaceholderSuffix}";
            result = result.Replace(placeholder, formatCodes[i], StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Extracts only the plain text portion, removing all format codes and braces.
    /// </summary>
    public string StripFormatCodes(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
            return string.Empty;

        var regex = FormatCodeRegex();
        var result = regex.Replace(rawText, string.Empty);
        result = StripBraces(result);
        return Regex.Replace(result, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// Checks if text contains any format codes.
    /// </summary>
    public bool HasFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return FormatCodeRegex().IsMatch(text);
    }

    /// <summary>
    /// Validates that format codes are intact after translation.
    /// </summary>
    /// <param name="originalText">The original raw text.</param>
    /// <param name="translatedText">The translated text.</param>
    /// <returns>True if all format codes from the original are present in the translation.</returns>
    public bool ValidateFormatCodeIntegrity(string originalText, string translatedText)
    {
        if (string.IsNullOrEmpty(originalText))
            return true;

        var originalCodes = ExtractFormatCodes(originalText);
        var translatedCodes = ExtractFormatCodes(translatedText);

        // All original codes should be present in translation
        return originalCodes.All(code => translatedCodes.Contains(code));
    }

    /// <summary>
    /// Extracts all format codes from text as a list.
    /// </summary>
    public List<string> ExtractFormatCodes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<string>();

        var regex = FormatCodeRegex();
        return regex.Matches(text).Select(m => m.Value).ToList();
    }

    private static string StripBraces(string text)
    {
        return text.Replace("{", "").Replace("}", "");
    }
}
