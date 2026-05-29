using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Interface for writing translated text back into DWG files offline (without AutoCAD).
/// </summary>
public interface IDwgWriterService
{
    /// <summary>
    /// Write translated text back into a DWG file and save as a new file.
    /// </summary>
    /// <param name="sourceFilePath">Path to the original DWG file.</param>
    /// <param name="outputFilePath">Path where the translated DWG should be saved.</param>
    /// <param name="entities">List of entities with translated text to apply.</param>
    /// <param name="cnToEn">True for Chinese→English, false for English→Chinese (used for font mapping).</param>
    /// <returns>Result with success/failure counts.</returns>
    DwgWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool cnToEn = true);
}