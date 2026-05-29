using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// Unit tests for ExcelService (export/import round-trip).
/// </summary>
public class ExcelServiceTests : IDisposable
{
    private readonly ExcelService _service;
    private readonly string _tempDir;

    public ExcelServiceTests()
    {
        _service = new ExcelService();
        _tempDir = Path.Combine(Path.GetTempPath(), $"excel_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ExportAndImport_RoundTrip_PreservesData()
    {
        var entities = new List<TextEntity>
        {
            new()
            {
                Handle = "1A2B",
                RawText = "轴承座",
                PlainText = "轴承座",
                TranslatedText = "Bearing Housing",
                GlossaryHit = true,
                Status = TranslationStatus.Translated,
                Notes = ""
            },
            new()
            {
                Handle = "3C4D",
                RawText = "\\P公差{\\fSymbol;±}0.05",
                PlainText = "公差0.05",
                TranslatedText = "\\PTolerance {\\fSymbol;±}0.05",
                GlossaryHit = false,
                Status = TranslationStatus.Translated,
                Notes = "格式码已保留"
            }
        };

        var filePath = Path.Combine(_tempDir, "test.xlsx");

        // Export
        await _service.ExportToExcelAsync(entities, filePath);
        Assert.True(File.Exists(filePath));

        // Import
        var imported = await _service.ImportFromExcelAsync(filePath);

        Assert.Equal(2, imported.Count);
        Assert.Equal("1A2B", imported[0].Handle);
        Assert.Equal("Bearing Housing", imported[0].TranslatedText);
        Assert.True(imported[0].GlossaryHit);
        Assert.Equal("3C4D", imported[1].Handle);
        Assert.Contains("Tolerance", imported[1].TranslatedText);
    }

    [Fact]
    public async Task ImportFromExcel_NonExistentFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.ImportFromExcelAsync("nonexistent.xlsx"));
    }

    [Fact]
    public async Task ExportToExcel_CreatesDirectoryIfMissing()
    {
        var subDir = Path.Combine(_tempDir, "sub", "dir");
        var filePath = Path.Combine(subDir, "test.xlsx");

        var entities = new List<TextEntity>
        {
            new() { Handle = "1", PlainText = "Test", TranslatedText = "Translated" }
        };

        await _service.ExportToExcelAsync(entities, filePath);

        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task ExportToExcel_EmptyList_CreatesFileWithHeaders()
    {
        var filePath = Path.Combine(_tempDir, "empty.xlsx");
        await _service.ExportToExcelAsync(new List<TextEntity>(), filePath);
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task ImportFromExcel_EmptyTranslation_MarksAsPending()
    {
        var entities = new List<TextEntity>
        {
            new()
            {
                Handle = "1",
                PlainText = "Test",
                TranslatedText = "", // Empty
                Status = TranslationStatus.Translated
            }
        };

        var filePath = Path.Combine(_tempDir, "empty_trans.xlsx");
        await _service.ExportToExcelAsync(entities, filePath);

        var imported = await _service.ImportFromExcelAsync(filePath);

        Assert.Single(imported);
        Assert.Equal(TranslationStatus.Pending, imported[0].Status); // Should be reset
    }
}
