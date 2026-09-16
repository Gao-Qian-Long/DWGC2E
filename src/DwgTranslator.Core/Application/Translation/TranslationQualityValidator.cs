namespace DwgTranslator.Core.Services;

using DwgTranslator.Core.Models;

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
        if (string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal) ||
            string.Equals(NormalizeMeaningfulText(source), NormalizeMeaningfulText(translated),
                StringComparison.OrdinalIgnoreCase))
            return TranslationFilter.IsNumericOnly(source) ||
                TranslationFilter.ShouldSkipTranslation(source, sourceLanguage, targetLanguage);

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase)) return true;

        // A response that still carries the SOURCE script is an echo, unless the two languages share
        // that script: Chinese into Japanese legitimately keeps Han characters.
        if (TranslationLanguages.ContainsScriptOf(translated, sourceLanguage) &&
            !TranslationLanguages.SharesScript(sourceLanguage, targetLanguage))
            return false;

        // Producing none of the target language's own script means the label was answered in
        // another script, typically the source one or plain Latin.
        if (TranslationLanguages.RequiresOwnScript(targetLanguage) &&
            !TranslationLanguages.ContainsScriptOf(translated, targetLanguage))
            return false;

        return true;
    }

    /// <summary>
    /// Removes punctuation and spacing before echo comparison. Models sometimes append a full stop
    /// or quote to unchanged source text; that is still an untranslated response and must not enter
    /// the cache or drawing.
    /// </summary>
    private static string NormalizeMeaningfulText(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool ContainsCjk(string text) => TranslationLanguages.ContainsHan(text);
}
