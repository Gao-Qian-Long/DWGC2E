using ClosedXML.Excel;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using Serilog;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Implements Excel export/import using ClosedXML (MIT licensed, safe for commercial use).
/// </summary>
public class ExcelService : IExcelService
{
    /// <inheritdoc/>
    public async Task ExportToExcelAsync(List<TextEntity> entities, string filePath, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Translations");

            // Headers
            worksheet.Cell(1, 1).Value = "Handle";
            worksheet.Cell(1, 2).Value = Strings.Get("ColOriginal");
            worksheet.Cell(1, 3).Value = Strings.Get("ColTranslation");
            worksheet.Cell(1, 4).Value = Strings.Get("ColGlossary");
            worksheet.Cell(1, 5).Value = Strings.Get("ColStatus");
            worksheet.Cell(1, 6).Value = Strings.Get("ColNotes");

            // Style headers
            var headerRange = worksheet.Range(1, 1, 1, 6);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

            // Data rows
            for (int i = 0; i < entities.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = i + 2;
                var entity = entities[i];

                worksheet.Cell(row, 1).Value = entity.Handle;
                worksheet.Cell(row, 2).Value = entity.RawText;
                worksheet.Cell(row, 3).Value = entity.TranslatedText;
                worksheet.Cell(row, 4).Value = entity.GlossaryHit ? "Y" : "N";
                worksheet.Cell(row, 5).Value = entity.Status.ToString();
                worksheet.Cell(row, 6).Value = entity.Notes;
            }

            // Auto-fit columns
            worksheet.Columns().AdjustToContents();

            // Save
            workbook.SaveAs(filePath);
            Log.Information("Exported {Count} entities to {Path}", entities.Count, filePath);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<TextEntity>> ImportFromExcelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Excel file not found", filePath);

            // Open with read-only sharing to avoid file lock conflicts
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(stream);

            var worksheet = workbook.Worksheet("Translations")
                ?? throw new InvalidOperationException("Worksheet 'Translations' not found");

            var entities = new List<TextEntity>();
            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;

            for (int row = 2; row <= lastRow; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var handle = worksheet.Row(row).Cell(1).GetString();
                if (string.IsNullOrEmpty(handle))
                    continue;

                var entity = new TextEntity
                {
                    Handle = handle,
                    RawText = worksheet.Row(row).Cell(2).GetString(),
                    TranslatedText = worksheet.Row(row).Cell(3).GetString(),
                    GlossaryHit = worksheet.Row(row).Cell(4).GetString() == "Y",
                    Status = Enum.TryParse<TranslationStatus>(worksheet.Row(row).Cell(5).GetString(), out var status)
                        ? status : TranslationStatus.Pending,
                    Notes = worksheet.Row(row).Cell(6).GetString()
                };

                if (string.IsNullOrEmpty(entity.TranslatedText))
                {
                    Log.Warning("Row {Row}: Empty translation for handle {Handle}", row, handle);
                    entity.Status = TranslationStatus.Pending;
                }

                entities.Add(entity);
            }

            Log.Information("Imported {Count} entities from {Path}", entities.Count, filePath);
            return entities;
        }, cancellationToken);
    }
}
