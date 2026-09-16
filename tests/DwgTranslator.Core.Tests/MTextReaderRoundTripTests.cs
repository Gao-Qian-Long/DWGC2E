using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class MTextReaderRoundTripTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dwgc2e-mtext-reader-" + Guid.NewGuid().ToString("N"));
    public MTextReaderRoundTripTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(".dwg", @"{\W0.75;Valve feedback}", "Valve feedback")]
    [InlineData(".dxf", @"{\W0.75;Valve feedback}", "Valve feedback")]
    [InlineData(".dwg", @"{\W1;阀门反馈}", "阀门反馈")]
    [InlineData(".dxf", @"{\W.75;W0.75; is ordinary text}", "W0.75; is ordinary text")]
    [InlineData(".dwg", "W0.75; is ordinary text", "W0.75; is ordinary text")]
    [InlineData(".dxf", @"{\W0.75;Valve} {\W1.25;feedback}", "Valve feedback")]
    public void WidthFormatNeverBecomesPlainTextAndRawFormattingSurvives(string extension, string raw, string expected)
    {
        var path = Path.Combine(root, "fixture" + extension);
        var document = new CadDocument();
        document.Entities.Add(new ACadSharp.Entities.MText { Value = raw, Height = 3.5, RectangleWidth = 30 });
        if (extension == ".dwg") DwgWriter.Write(path, document); else DxfWriter.Write(path, document, false);
        var bytes = File.ReadAllBytes(path);
        var entity = Assert.Single(new DwgReaderService().ExtractFromFile(path));
        Assert.Equal(expected, entity.PlainText);
        Assert.Equal(raw, entity.RawText);
        Assert.Equal(raw, entity.FormatTemplate);
        Assert.Equal("MText", entity.EntityType);
        Assert.Equal(30, entity.MTextRectangleWidth);
        Assert.Equal(Path.GetFullPath(path), entity.SourceFilePath);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(@"Line one\PLine two")]
    [InlineData(@"\U+9600\U+95E8")]
    [InlineData(@"{\C1;Valve} %%c20")]
    [InlineData(@"W0.75; literal text")]
    public void WidthCleanupPreservesExistingDecoderBehavior(string raw)
    {
        var path = Path.Combine(root, "decoder.dwg");
        var document = new CadDocument();
        document.Entities.Add(new ACadSharp.Entities.MText { Value = raw, Height = 3.5 });
        document.Entities.Add(new ACadSharp.Entities.MText { Value = "{\\W0.75;" + raw + "}", Height = 3.5 });
        DwgWriter.Write(path, document);
        var entities = new DwgReaderService().ExtractFromFile(path);
        Assert.Equal(2, entities.Count);
        Assert.Equal(entities[0].PlainText, entities[1].PlainText);
        Assert.Equal("{\\W0.75;" + raw + "}", entities[1].FormatTemplate);
    }
    public void Dispose() => Directory.Delete(root, true);
}
