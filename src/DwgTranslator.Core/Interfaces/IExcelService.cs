using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for exporting/importing translation data via Excel.
/// </summary>
public interface IExcelService
{
    /// <summary>Export text entities to an Excel file for review.</summary>
    Task ExportToExcelAsync(List<TextEntity> entities, string filePath, CancellationToken cancellationToken = default);

    /// <summary>Import reviewed translations from an Excel file.</summary>
    Task<List<TextEntity>> ImportFromExcelAsync(string filePath, CancellationToken cancellationToken = default);
}
