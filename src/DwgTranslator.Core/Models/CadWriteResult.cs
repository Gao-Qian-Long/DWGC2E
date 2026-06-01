namespace DwgTranslator.Core.Models;

/// <summary>
/// Result of a CAD (DWG/DXF) writeback operation.
/// Used for both offline (ACadSharp) and AutoCAD COM interop writeback paths.
/// </summary>
public class CadWriteResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsSuccess => SuccessCount > 0;
}
