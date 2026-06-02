using Autodesk.AutoCAD.DatabaseServices;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Result of a writeback operation.
/// </summary>
public class WritebackResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public int SkippedCount { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Result of a single entity replacement.
/// </summary>
internal class EntityReplaceResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public Entity? ModifiedEntity { get; set; }
    public BlockTableRecord? OwningBlock { get; set; }
    public double OriginalHeight { get; set; }
    public double CurrentHeight { get; set; }
}
