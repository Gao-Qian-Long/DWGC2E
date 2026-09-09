using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Service for interacting with AutoCAD via COM interop to perform precise DWG writeback.
/// </summary>
public interface IAutoCadInteropService
{
    /// <summary>
    /// Check if AutoCAD is available for COM interop (installed and running).
    /// </summary>
    bool IsAutoCADAvailable(AppConfig config);

    /// <summary>
    /// Execute writeback of translated entities via AutoCAD COM interop.
    /// Creates a LISP script, sends it to AutoCAD, and monitors the done-signal file.
    /// </summary>
    Task<CadWriteResult> WritebackViaAutoCadAsync(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities,
        bool cnToEn,
        AppConfig config,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
