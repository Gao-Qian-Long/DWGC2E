using DwgTranslator.Core.Models;
using System.Threading;

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
    /// <param name="targetIsCjk">True when the target language needs a CJK-capable font.</param>
    /// <returns>Result with success/failure counts.</returns>
    CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool targetIsCjk = true, CancellationToken cancellationToken = default);
}
