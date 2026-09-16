using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for reading text entities from DXF files without AutoCAD.
/// </summary>
public interface IDxfReaderService
{
    /// <summary>Extract all text entities from a DXF file.</summary>
    List<TextEntity> ExtractFromFile(string filePath);

    /// <summary>Extract text entities from multiple DXF files.</summary>
    Dictionary<string, List<TextEntity>> ExtractFromFiles(IEnumerable<string> filePaths);
}
