namespace DwgTranslator.Core.Services;

/// <summary>
/// Rejects empty, echoed, or source-language responses before they can be cached
/// or written back to a drawing.
/// </summary>
public static class TranslationQualityValidator
{
    public static bool IsAcceptable(string source, string translated, string sourceLanguage, string targetLanguage, bool allowPlaceholders = false)
    {
        if (string.IsNullOrWhiteSpace(translated)) return false;
        if (!allowPlaceholders && (translated.Contains("__FMT_",StringComparison.Ordinal) || translated.Contains("__GLOSSARY_",StringComparison.Ordinal))) return false;
        if (string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal))
            return TranslationFilter.IsNumericOnly(source) ||
                TranslationFilter.ShouldSkipTranslation(source, sourceLanguage, targetLanguage);

        int sourceCjk = source.Count(IsCjk);
        int translatedCjk = translated.Count(IsCjk);
        if (string.Equals(targetLanguage, "EN", StringComparison.OrdinalIgnoreCase) && sourceCjk > 0)
            return translatedCjk == 0;

        if (string.Equals(targetLanguage, "ZH", StringComparison.OrdinalIgnoreCase) &&
            source.Any(char.IsLetter) && !source.Any(IsCjk))
            return translatedCjk > 0;

        return true;
    }

    public static bool ContainsCjk(string text) => text.Any(IsCjk);

    private static bool IsCjk(char c) =>
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\uF900' && c <= '\uFAFF');
}
