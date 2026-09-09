namespace DwgTranslator.Core.Models;

/// <summary>
/// Represents a source-target translation pair for a text entity.
/// </summary>
public class TranslationPair
{
    public string Handle { get; set; } = string.Empty;
    /// <summary>Source drawing path; Handle is only unique inside one drawing.</summary>
    public string SourceFilePath { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public bool GlossaryHit { get; set; }
    public TranslationStatus Status { get; set; } = TranslationStatus.Pending;
    public string ErrorMessage { get; set; } = string.Empty;
}
