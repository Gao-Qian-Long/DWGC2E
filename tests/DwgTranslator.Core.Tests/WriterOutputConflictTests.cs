using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

// Exercise actual CAD serialization/commit, not the pipeline's fake writer.
public sealed class WriterOutputConflictTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-writer-conflict-" + Guid.NewGuid().ToString("N"));
    public WriterOutputConflictTests() => Directory.CreateDirectory(root);

    private (string Source, List<TextEntity> Entities) CreateDrawing(string extension)
    {
        var source = Path.Combine(root, "source" + extension);
        var doc = new CadDocument();
        doc.Entities.Add(new ACadSharp.Entities.TextEntity { Value = "Motor", Height = 3 });
        if (extension == ".dxf") DxfWriter.Write(source, doc, false);
        else DwgWriter.Write(source, doc);
        var entities = new DwgReaderService().ExtractFromFile(source);
        Assert.Single(entities);
        entities[0].TranslatedText = "Engine";
        entities[0].Status = TranslationStatus.Translated;
        return (source, entities);
    }

    [Theory]
    [InlineData(".dxf", "skip")]
    [InlineData(".dxf", "rename")]
    [InlineData(".dxf", "overwrite")]
    [InlineData(".dwg", "skip")]
    [InlineData(".dwg", "rename")]
    [InlineData(".dwg", "overwrite")]
    public void FileCreatedAfterPlanningIsOnlyReplacedWithExplicitOverwrite(string extension, string policy)
    {
        var (source, entities) = CreateDrawing(extension);
        var original = File.ReadAllBytes(source);
        var plan = new OutputPathResolver(new AppConfig { ExportDirectory = ".", OutputNamingPattern = "{name}_{lang}", DuplicatePolicy = policy }).Resolve(source, "EN");
        Assert.False(plan.ShouldSkip);
        Assert.False(File.Exists(plan.OutputPath));
        // A competing application creates the destination after our output plan was made.
        File.WriteAllText(plan.OutputPath, "external user file");
        var result = new DwgWriterService().WriteTranslations(source, plan.OutputPath, entities, false,
            options: new WritebackOptions { OverwriteExisting = policy == "overwrite" });
        if (policy == "overwrite")
        {
            Assert.Empty(result.Errors);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal("Engine", Assert.Single(new DwgReaderService().ExtractFromFile(plan.OutputPath)).PlainText);
        }
        else
        {
            Assert.NotEmpty(result.Errors);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailCount);
            Assert.Equal("external user file", File.ReadAllText(plan.OutputPath));
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData(".dxf")]
    [InlineData(".dwg")]
    public void LockedOutputPreservesBothFilesAndRemovesTemporaryOutput(string extension)
    {
        var (source, entities) = CreateDrawing(extension);
        var original = File.ReadAllBytes(source);
        var output = Path.Combine(root, "locked" + extension);
        File.WriteAllText(output, "previous output");
        using (File.Open(output, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = new DwgWriterService().WriteTranslations(source, output, entities, false,
                options: new WritebackOptions { OverwriteExisting = true });
            Assert.NotEmpty(result.Errors);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailCount);
        }
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(root, true);
}
