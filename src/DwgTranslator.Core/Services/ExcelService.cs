using DwgTranslator.Core.Models;
using OfficeOpenXml;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements Excel export/import using EPPlus.
/// </summary>
public class ExcelService : IExcelService
{
    static ExcelService()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    /// <inheritdoc/>
    public async Task ExportToExcelAsync(List<TextEntity> entities, string filePath, CancellationToken cancellationToken = default)
    {
        using var package = new ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("Translations");

        // Headers
        worksheet.Cells[1, 1].Value = "Handle";
        worksheet.Cells[1, 2].Value = "原文";
        worksheet.Cells[1, 3].Value = "译文";
        worksheet.Cells[1, 4].Value = "术语命中";
        worksheet.Cells[1, 5].Value = "状态";
        worksheet.Cells[1, 6].Value = "备注";

        // Style headers
        using (var range = worksheet.Cells[1, 1, 1, 6])
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
        }

        // Data rows
        for (int i = 0; i < entities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = i + 2;
            var entity = entities[i];

            worksheet.Cells[row, 1].Value = entity.Handle;
            worksheet.Cells[row, 2].Value = entity.RawText;
            worksheet.Cells[row, 3].Value = entity.TranslatedText;
            worksheet.Cells[row, 4].Value = entity.GlossaryHit ? "Y" : "N";
            worksheet.Cells[row, 5].Value = entity.Status.ToString();
            worksheet.Cells[row, 6].Value = entity.Notes;
        }

        // Auto-fit columns
        worksheet.Cells.AutoFitColumns();

        // Save
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await package.SaveAsAsync(new FileInfo(filePath), cancellationToken);
        Log.Information("Exported {Count} entities to {Path}", entities.Count, filePath);
    }

    /// <inheritdoc/>
    public Task<List<TextEntity>> ImportFromExcelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Excel file not found", filePath);

        // Open with read-only sharing to avoid file lock conflicts
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var package = new ExcelPackage(stream);

        var worksheet = package.Workbook.Worksheets["Translations"]
            ?? throw new InvalidOperationException("Worksheet 'Translations' not found");

        var entities = new List<TextEntity>();
        var rowCount = worksheet.Dimension?.Rows ?? 0;

        for (int row = 2; row <= rowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = worksheet.Cells[row, 1].GetValue<string>();
            if (string.IsNullOrEmpty(handle))
                continue;

            var entity = new TextEntity
            {
                Handle = handle,
                RawText = worksheet.Cells[row, 2].GetValue<string>() ?? string.Empty,
                TranslatedText = worksheet.Cells[row, 3].GetValue<string>() ?? string.Empty,
                GlossaryHit = worksheet.Cells[row, 4].GetValue<string>() == "Y",
                Status = Enum.TryParse<TranslationStatus>(worksheet.Cells[row, 5].GetValue<string>(), out var status)
                    ? status : TranslationStatus.Pending,
                Notes = worksheet.Cells[row, 6].GetValue<string>() ?? string.Empty
            };

            if (string.IsNullOrEmpty(entity.TranslatedText))
            {
                Log.Warning("Row {Row}: Empty translation for handle {Handle}", row, handle);
                entity.Status = TranslationStatus.Pending;
            }

            entities.Add(entity);
        }

        Log.Information("Imported {Count} entities from {Path}", entities.Count, filePath);
        return Task.FromResult(entities);
    }
}
