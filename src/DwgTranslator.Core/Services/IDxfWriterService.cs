using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Interface for writing translated text back into DXF files offline (without AutoCAD).
/// </summary>
public interface IDxfWriterService
{
    /// <summary>
    /// Write translated text back into a DXF file and save as a new file.
    /// </summary>
    /// <param name="sourceFilePath">Path to the original DXF file.</param>
    /// <param name="outputFilePath">Path where the translated DXF should be saved.</param>
    /// <param name="entities">List of entities with translated text to apply.</param>
    /// <param name="cnToEn">True for Chinese→English, false for English→Chinese (used for font mapping).</param>
    /// <returns>Result with success/failure counts.</returns>
    DxfWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool cnToEn = true);
}

/// <summary>
/// Result of a DXF writeback operation.
/// </summary>
public class DxfWriteResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsSuccess => SuccessCount > 0;
}
