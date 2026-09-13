namespace DwgTranslator.Core.Models;

/// <summary>Explicit filesystem permissions for a single output operation.</summary>
public sealed class WritebackOptions
{
    public bool OverwriteExisting { get; set; }
    public bool BackupSource { get; set; } = true;
}
