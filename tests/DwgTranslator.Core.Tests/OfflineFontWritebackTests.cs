using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Tables;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Core.Tests;

public sealed class OfflineFontWritebackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dwgc2e-font-writeback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OfflineWriterMapsTheActualStyleFontAndWritesAnExplicitTargetFontFile()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.dwg");
        var output = Path.Combine(_root, "output.dwg");
        var document = new CadDocument();
        var sourceStyle = document.TextStyles["Standard"];
        sourceStyle.Filename = "simsun.ttc";
        sourceStyle.Height = 0;
        sourceStyle.Width = 1;
        document.Entities.Add(new ACadSharp.Entities.TextEntity
        {
            Value = "阀门反馈",
            Height = 3.5,
            Style = sourceStyle
        });
        DwgWriter.Write(source, document);

        var entity = Assert.Single(new DwgReaderService().ExtractFromFile(source));
        entity.TranslatedText = "Valve feedback";
        entity.Status = TranslationStatus.Translated;
        var result = new DwgWriterService().WriteTranslations(source, output, [entity], targetIsCjk: false);

        Assert.Equal(1, result.SuccessCount);
        var written = DwgReader.Read(output).Entities.OfType<ACadSharp.Entities.TextEntity>().Single();
        Assert.Equal("Valve feedback", written.Value);
        Assert.Equal("DWGC2E_Arial", written.Style.Name);
        Assert.Equal("arial.ttf", written.Style.Filename, ignoreCase: true);
        Assert.Equal(string.Empty, written.Style.BigFontFilename);
        Assert.Equal(1, written.Style.Width);
        Assert.Equal(0, written.Style.Height);
    }

    [Theory]
    [InlineData("Standard", "simsun.ttc", "gbcbig.shx", false, "Arial")]
    [InlineData("Standard", "txt.shx", "gbcbig.shx", false, "Arial")]
    [InlineData("T1", "hztxt.shx", "", false, "Arial")]
    [InlineData("T1", "txt.shx", "", true, "SimHei")]
    [InlineData("Label", "arial.ttf", "", true, "SimHei")]
    public void FontMappingInspectsTheActualCadFontFiles(string styleName, string primary, string big,
        bool targetIsCjk, string expected) =>
        Assert.Equal(expected, FontMapper.MapFontName(styleName, primary, big, targetIsCjk));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
