using System.Text.Json;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements glossary loading, matching, and placeholder replacement.
/// </summary>
public class GlossaryService : IGlossaryService
{
    private readonly List<GlossaryEntry> _entries = new();
    private const string PlaceholderPrefix = "__GLOSSARY_";
    private const string PlaceholderSuffix = "__";

    /// <inheritdoc/>
    public async Task LoadGlossaryAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Log.Warning("Glossary file not found: {Path}", filePath);
            return;
        }

        var json = await File.ReadAllTextAsync(filePath);
        var entries = JsonSerializer.Deserialize<List<GlossaryEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (entries != null)
        {
            _entries.Clear();
            _entries.AddRange(entries.OrderByDescending(e => e.Source.Length)); // Longest match first
            Log.Information("Loaded {Count} glossary entries from {Path}", _entries.Count, filePath);
        }
    }

    /// <inheritdoc/>
    public List<GlossaryMatch> MatchTerms(string text)
    {
        if (string.IsNullOrEmpty(text) || _entries.Count == 0)
            return new List<GlossaryMatch>();

        var matches = new List<GlossaryMatch>();
        var occupied = new bool[text.Length]; // Track which positions are already matched
        var matchIndex = 0;

        foreach (var entry in _entries)
        {
            int searchStart = 0;
            int pos;
            while ((pos = text.IndexOf(entry.Source, searchStart, StringComparison.Ordinal)) >= 0)
            {
                // Check if any position in this range is already occupied
                bool overlaps = false;
                for (int i = pos; i < pos + entry.Source.Length && i < occupied.Length; i++)
                {
                    if (occupied[i]) { overlaps = true; break; }
                }

                if (!overlaps)
                {
                    matchIndex++;
                    matches.Add(new GlossaryMatch
                    {
                        Index = matchIndex,
                        SourceTerm = entry.Source,
                        TargetTerm = entry.Target,
                        Placeholder = $"{PlaceholderPrefix}{matchIndex}{PlaceholderSuffix}",
                        Position = pos
                    });

                    // Mark positions as occupied
                    for (int i = pos; i < pos + entry.Source.Length && i < occupied.Length; i++)
                        occupied[i] = true;
                }

                searchStart = pos + entry.Source.Length;
            }
        }

        return matches;
    }

    /// <inheritdoc/>
    public string ReplaceWithPlaceholders(string text, List<GlossaryMatch> matches)
    {
        if (matches.Count == 0)
            return text;

        var result = text;
        // Replace from end to start to preserve positions
        foreach (var match in matches.OrderByDescending(m => m.Position))
        {
            result = result[..match.Position] + match.Placeholder + result[(match.Position + match.SourceTerm.Length)..];
        }
        return result;
    }

    /// <inheritdoc/>
    public string RestorePlaceholders(string text, List<GlossaryMatch> matches)
    {
        if (string.IsNullOrEmpty(text) || matches.Count == 0)
            return text;

        var result = text;
        foreach (var match in matches)
        {
            result = result.Replace(match.Placeholder, match.TargetTerm, StringComparison.Ordinal);
        }
        return result;
    }

    /// <inheritdoc/>
    public IReadOnlyList<GlossaryEntry> GetAllEntries() => _entries.AsReadOnly();
}
