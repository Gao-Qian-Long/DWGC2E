namespace DwgTranslator.Core.Models;

/// <summary>
/// A glossary entry for deterministic term replacement.
/// </summary>
public class GlossaryEntry
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
