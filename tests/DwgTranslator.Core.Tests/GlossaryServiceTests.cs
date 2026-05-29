using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// Unit tests for GlossaryService.
/// </summary>
public class GlossaryServiceTests : IDisposable
{
    private readonly GlossaryService _service;
    private readonly string _tempDir;

    public GlossaryServiceTests()
    {
        _service = new GlossaryService();
        _tempDir = Path.Combine(Path.GetTempPath(), $"glossary_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string CreateTempGlossary(List<GlossaryEntry> entries)
    {
        var path = Path.Combine(_tempDir, "test_glossary.json");
        var json = JsonSerializer.Serialize(entries);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task LoadGlossary_ValidFile_LoadsEntries()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "轴承", Target = "Bearing", Category = "mechanical" },
            new() { Source = "齿轮", Target = "Gear", Category = "mechanical" }
        };
        var path = CreateTempGlossary(entries);

        await _service.LoadGlossaryAsync(path);

        Assert.Equal(2, _service.GetAllEntries().Count);
    }

    [Fact]
    public async Task LoadGlossary_NonExistentFile_DoesNotThrow()
    {
        await _service.LoadGlossaryAsync("nonexistent.json");
        Assert.Empty(_service.GetAllEntries());
    }

    [Fact]
    public async Task MatchTerms_ExactMatch_FindsIt()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "轴承", Target = "Bearing" }
        };
        await _service.LoadGlossaryAsync(CreateTempGlossary(entries));

        var matches = _service.MatchTerms("这是轴承");
        Assert.Single(matches);
        Assert.Equal("轴承", matches[0].SourceTerm);
        Assert.Equal("Bearing", matches[0].TargetTerm);
    }

    [Fact]
    public async Task MatchTerms_NoMatch_ReturnsEmpty()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "轴承", Target = "Bearing" }
        };
        await _service.LoadGlossaryAsync(CreateTempGlossary(entries));

        var matches = _service.MatchTerms("这是螺丝");
        Assert.Empty(matches);
    }

    [Fact]
    public async Task MatchTerms_MultipleMatches_FindsAll()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "轴承", Target = "Bearing" },
            new() { Source = "座", Target = "Housing" }
        };
        await _service.LoadGlossaryAsync(CreateTempGlossary(entries));

        var matches = _service.MatchTerms("轴承座");
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task MatchTerms_LongestMatchFirst_PrioritizesCorrectly()
    {
        var entries = new List<GlossaryEntry>
        {
            new() { Source = "轴承", Target = "Bearing" },
            new() { Source = "轴承座", Target = "Bearing Housing" }
        };
        await _service.LoadGlossaryAsync(CreateTempGlossary(entries));

        var matches = _service.MatchTerms("轴承座");
        // "轴承座" (3 chars) should be matched before "轴承" (2 chars)
        Assert.Contains(matches, m => m.SourceTerm == "轴承座");
    }

    [Fact]
    public async Task ReplaceWithPlaceholders_ReplacesCorrectly()
    {
        var matches = new List<GlossaryMatch>
        {
            new() { Index = 0, SourceTerm = "轴承", Placeholder = "__GLOSSARY_1__", Position = 2 }
        };

        var result = _service.ReplaceWithPlaceholders("这是轴承", matches);
        Assert.Equal("这是__GLOSSARY_1__", result);
    }

    [Fact]
    public async Task RestorePlaceholders_RestoresCorrectly()
    {
        var matches = new List<GlossaryMatch>
        {
            new() { Placeholder = "__GLOSSARY_1__", TargetTerm = "Bearing" }
        };

        var result = _service.RestorePlaceholders("这是__GLOSSARY__", matches);
        Assert.Equal("这是__GLOSSARY__", result); // Different placeholder, no change
    }

    [Fact]
    public async Task RestorePlaceholders_CorrectPlaceholder_Restores()
    {
        var matches = new List<GlossaryMatch>
        {
            new() { Placeholder = "__GLOSSARY_1__", TargetTerm = "Bearing" }
        };

        var result = _service.RestorePlaceholders("这是__GLOSSARY_1__", matches);
        Assert.Equal("这是Bearing", result);
    }

    [Fact]
    public async Task EmptyText_ReturnsEmptyMatches()
    {
        var matches = _service.MatchTerms(string.Empty);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task EmptyGlossary_ReturnsEmptyMatches()
    {
        var entries = new List<GlossaryEntry>();
        await _service.LoadGlossaryAsync(CreateTempGlossary(entries));

        var matches = _service.MatchTerms("轴承");
        Assert.Empty(matches);
    }
}
