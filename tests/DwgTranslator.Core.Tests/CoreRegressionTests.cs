using System.Text.Json;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;

namespace DwgTranslator.Core.Tests;

public sealed class CoreRegressionTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(
        Path.GetTempPath(), $"dwgtranslator-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void FontMapping_UsesTargetDirection()
    {
        Assert.Equal("Arial", FontMapper.MapFontName("SimSun", targetIsCjk: false));
        Assert.Equal("SimHei", FontMapper.MapFontName("Arial", targetIsCjk: true));
    }

    [Fact]
    public void TranslationQuality_RejectsPunctuatedEcho()
    {
        Assert.False(TranslationQualityValidator.IsAcceptable(
            "Motor stop", "Motor stop!", "EN", "DE"));
    }

    [Fact]
    public void Cache_IsolatedByLanguageDirection()
    {
        var cache = new TranslationConsistencyService(_cachePath);
        cache.AddToCache("轴承", "Bearing", "ZH>EN");

        Assert.True(cache.TryGetMatch("轴承", "ZH>EN", out var english));
        Assert.Equal("Bearing", english);
        Assert.False(cache.TryGetMatch("轴承", "ZH>DE", out _));
    }

    [Fact]
    public void ConfigContract_ReadsAndWritesCamelCase()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            "{\"deepSeekModel\":\"custom\",\"targetLanguage\":\"DE\"}", AppConfigJson.ReadOptions);
        Assert.Equal("custom", config!.DeepSeekModel);
        Assert.Equal("DE", config.TargetLanguage);

        var json = JsonSerializer.Serialize(config, AppConfigJson.WriteOptions);
        Assert.Contains("\"deepSeekModel\"", json);
        Assert.DoesNotContain("\"DeepSeekModel\"", json);
    }

    [Fact]
    public async Task MissingGlossary_ClearsPreviouslyLoadedEntries()
    {
        var glossaryFile = Path.ChangeExtension(_cachePath, ".glossary.json");
        await File.WriteAllTextAsync(glossaryFile, "[{\"source\":\"轴承\",\"target\":\"Bearing\"}]");
        try
        {
            var glossary = new GlossaryService();
            await glossary.LoadGlossaryAsync(glossaryFile);
            Assert.Single(glossary.GetAllEntries());

            await glossary.LoadGlossaryAsync(glossaryFile + ".missing");
            Assert.Empty(glossary.GetAllEntries());
        }
        finally
        {
            File.Delete(glossaryFile);
        }
    }

    [Theory]
    [InlineData("ZH", "EN", "mechanical_zh_en.json")]
    [InlineData("ZH-TW", "JA", "mechanical_zh_tw_ja.json")]
    public void GlossaryName_IsDirectionSpecific(string source, string target, string expected) =>
        Assert.Equal(expected, TranslationLanguages.GlossaryFileName(source, target));

    [Fact]
    public void BatchExport_UsesStampedSourcesWithoutAskingForThemAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "dwgtranslator-batch");
        var entities = new[]
        {
            new TextEntity { SourceFilePath = Path.Combine(root, "a.dwg") },
            new TextEntity { SourceFilePath = Path.Combine(root, "A.dwg") },
            new TextEntity { SourceFilePath = Path.Combine(root, "b.dxf") }
        };

        var sources = BatchExportPlanner.GetKnownSources(entities);

        Assert.Equal(2, sources.Count);
        Assert.Equal(Path.Combine(root, "a_translated.dwg"),
            BatchExportPlanner.CreateDestinationPath(sources[0], root));
        Assert.Equal(Path.Combine(root, "b_translated.dxf"),
            BatchExportPlanner.CreateDestinationPath(sources[1], root));

        var duplicateNames = BatchExportPlanner.CreateDestinationMap(new[]
        {
            Path.Combine(root, "one", "panel.dwg"),
            Path.Combine(root, "two", "panel.dwg")
        }, root);
        Assert.Equal(Path.Combine(root, "panel_translated.dwg"), duplicateNames.Values.First());
        Assert.Equal(Path.Combine(root, "panel_translated_2.dwg"), duplicateNames.Values.Last());
    }

    [Fact]
    public void VerticalColumn_PreservesDirectionAcrossTargetScripts()
    {
        Assert.True(VerticalTextLayout.IsCharacterColumn("35A共挤启动", 0, 7.63, 9.24));

        Assert.Equal(Math.PI / 2, VerticalTextLayout.TargetRotation(0, targetIsCjk: false));
        Assert.Equal("Co-extrusion Startup",
            VerticalTextLayout.FormatTranslatedColumn("Co-extrusion\\PStartup", targetIsCjk: false));

        Assert.Equal(0, VerticalTextLayout.TargetRotation(0, targetIsCjk: true));
        Assert.Equal(@"35A\P共\P挤\P启\P动",
            VerticalTextLayout.FormatTranslatedColumn("35A共挤启动", targetIsCjk: true));
    }

    [Theory]
    [InlineData(10, 0.4)]
    [InlineData(2, 0.08)]
    [InlineData(0.1, 0.02)]
    public void GeometryClearance_LeavesVisibleRoomInsideCellBorders(double textHeight, double expected) =>
        Assert.Equal(expected, WritebackConstants.GeometryClearance(textHeight), precision: 8);

    public void Dispose()
    {
        try { File.Delete(_cachePath); } catch { }
    }
}
