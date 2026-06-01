namespace DwgTranslator.Core.Translation;

/// <summary>
/// Interface for parsing and preserving MTEXT/DIMENSION format control codes during translation.
/// </summary>
public interface IFormatCodeParser
{
    /// <summary>
    /// Extracts format codes from text and returns a template with placeholders.
    /// </summary>
    /// <param name="rawText">The raw text containing format codes.</param>
    /// <returns>A tuple of (plainText, formatTemplate, formatCodes).</returns>
    (string PlainText, string FormatTemplate, List<string> FormatCodes) Parse(string rawText);

    /// <summary>
    /// Extracts only the plain text portion, removing all format codes and braces.
    /// </summary>
    string StripFormatCodes(string rawText);

    /// <summary>
    /// Restores format codes in translated text using the stored format codes.
    /// </summary>
    /// <param name="translatedText">The translated text with placeholders.</param>
    /// <param name="formatCodes">The original format codes to restore.</param>
    /// <returns>The text with format codes restored.</returns>
    string Restore(string translatedText, List<string> formatCodes);

    /// <summary>
    /// Checks if text contains any format codes.
    /// </summary>
    bool HasFormatCodes(string text);

    /// <summary>
    /// Validates that format codes are intact after translation.
    /// </summary>
    bool ValidateFormatCodeIntegrity(string originalText, string translatedText);

    /// <summary>
    /// Extracts all format codes from text as a list.
    /// </summary>
    List<string> ExtractFormatCodes(string text);
}
