namespace DwgTranslator.Core.Models;

/// <summary>
/// Lightweight CAD-host contract. The CLR 4.x plugin does not need WPF/MVVM
/// notifications, so it intentionally avoids CommunityToolkit dependencies.
/// </summary>
public class TextEntity
{
    public string Handle { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public string FormatTemplate { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public double Height { get; set; }
    public double Rotation { get; set; }
    public string TextStyleName { get; set; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public bool IsXref { get; set; }
    public Point3d Position { get; set; }
    public double OriginalWidth { get; set; }
    public double OriginalHeight { get; set; }
    public double MTextRectangleWidth { get; set; }
    public double MTextLineSpacing { get; set; } = 1.0;
    public int MTextLineSpacingStyle { get; set; } = 1;
    public int MTextLineCount { get; set; } = 1;
    public bool MTextHasHardBreaks { get; set; }
    public string SourceFilePath { get; set; } = string.Empty;
    public TranslationStatus Status { get; set; } = TranslationStatus.Pending;
    public string TranslatedText { get; set; } = string.Empty;
    public bool GlossaryHit { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public struct Point3d
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    public Point3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public enum TranslationStatus
{
    Pending,
    GlossaryMatched,
    Translated,
    TranslationFailed,
    Reviewed,
    WritebackSuccess,
    WritebackFailed,
    Skipped
}
