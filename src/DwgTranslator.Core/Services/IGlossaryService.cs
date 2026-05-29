using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for managing translation glossaries.
/// </summary>
public interface IGlossaryService
{
    /// <summary>Load glossary entries from a file path.</summary>
    Task LoadGlossaryAsync(string filePath);

    /// <summary>Match text against glossary and return matched entries.</summary>
    List<GlossaryMatch> MatchTerms(string text);

    /// <summary>Replace matched terms with placeholders.</summary>
    string ReplaceWithPlaceholders(string text, List<GlossaryMatch> matches);

    /// <summary>Restore placeholders with translated terms.</summary>
    string RestorePlaceholders(string text, List<GlossaryMatch> matches);

    /// <summary>Get all loaded entries.</summary>
    IReadOnlyList<GlossaryEntry> GetAllEntries();
}

/// <summary>
/// A glossary match result.
/// </summary>
public class GlossaryMatch
{
    public int Index { get; set; }
    public string SourceTerm { get; set; } = string.Empty;
    public string TargetTerm { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public int Position { get; set; }
}
