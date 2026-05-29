using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for reading text entities from DWG files without AutoCAD.
/// </summary>
public interface IDwgReaderService
{
    /// <summary>Extract all text entities from a DWG file.</summary>
    List<TextEntity> ExtractFromFile(string filePath);

    /// <summary>Extract text entities from multiple DWG files.</summary>
    Dictionary<string, List<TextEntity>> ExtractFromFiles(IEnumerable<string> filePaths);
}
