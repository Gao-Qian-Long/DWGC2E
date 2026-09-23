using System.Text.Json.Serialization;

namespace DwgTranslator.Core.Models;

public enum TranslationProjectEntityState
{
    Pending,
    Translated,
    Reviewed,
    Failed,
    Skipped
}

public sealed class TranslationProject
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public Dictionary<string, string> TranslationConfig { get; set; } = new();
    public List<TranslationProjectDrawing> Drawings { get; set; } = new();
    public List<TranslationProjectExport> ExportHistory { get; set; } = new();
}

public sealed class TranslationProjectDrawing
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public List<TranslationProjectEntry> Entries { get; set; } = new();
}

public sealed class TranslationProjectEntry
{
    public string Handle { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public TranslationStatus Status { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool GlossaryHit { get; set; }
    public bool ManuallyEdited { get; set; }
}

public sealed class TranslationProjectExport
{
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public string OutputDirectory { get; set; } = string.Empty;
    public string WritebackMode { get; set; } = string.Empty;
    public List<TranslationProjectExportFile> Files { get; set; } = new();
}

public sealed class TranslationProjectExportFile
{
    public string SourcePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public bool Skipped { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class TranslationProjectSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime ModifiedAtUtc { get; set; }
    public int DrawingCount { get; set; }
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>
    /// 项目下拉里显示的文案（用户批注 2026-09-23：「能让用户找到历史翻译的文件，可以修改校对，然后重新导出」）。
    /// 原来下拉只显示 Name，多个批次名字相近时分不清哪个是要找的历史项目；
    /// 这里补上张数与最后修改时间，便于一眼认出历史批次。
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var when = ModifiedAtUtc == default ? string.Empty : $" · {ModifiedAtUtc.ToLocalTime():MM-dd HH:mm}";
            return $"{Name} · {DrawingCount} 张{when}";
        }
    }
}
