using CommunityToolkit.Mvvm.ComponentModel;

namespace DwgTranslator.Core.Models;

/// <summary>
/// Represents a text entity extracted from a DWG file.
/// Observable for WPF MVVM data binding.
/// </summary>
public partial class TextEntity : ObservableObject
{
    /// <summary>Unique handle for writeback定位.</summary>
    [ObservableProperty]
    private string _handle = string.Empty;

    /// <summary>Raw text with format codes preserved.</summary>
    [ObservableProperty]
    private string _rawText = string.Empty;

    /// <summary>Plain text with format codes removed (sent to translation).</summary>
    [ObservableProperty]
    private string _plainText = string.Empty;

    /// <summary>Format code template with placeholders for translated text.</summary>
    [ObservableProperty]
    private string _formatTemplate = string.Empty;

    /// <summary>Entity type: DBText, MText, AttributeReference, Dimension, MLeader, Table, ArcAlignedText.</summary>
    [ObservableProperty]
    private string _entityType = string.Empty;

    /// <summary>Text height.</summary>
    [ObservableProperty]
    private double _height;

    /// <summary>Text rotation in radians.</summary>
    [ObservableProperty]
    private double _rotation;

    /// <summary>Name of the text style used.</summary>
    [ObservableProperty]
    private string _textStyleName = string.Empty;

    /// <summary>Block name this entity belongs to (empty = model space).</summary>
    [ObservableProperty]
    private string _blockName = string.Empty;

    /// <summary>Whether this entity comes from an external reference.</summary>
    [ObservableProperty]
    private bool _isXref;

    /// <summary>Position of the text entity.</summary>
    [ObservableProperty]
    private Point3d _position;

    /// <summary>Original width for auto-scaling calculations.</summary>
    [ObservableProperty]
    private double _originalWidth;

    /// <summary>Translation status.</summary>
    [ObservableProperty]
    private TranslationStatus _status = TranslationStatus.Pending;

    /// <summary>Translated text (filled after translation).</summary>
    [ObservableProperty]
    private string _translatedText = string.Empty;

    /// <summary>Whether the text was matched by glossary.</summary>
    [ObservableProperty]
    private bool _glossaryHit;

    /// <summary>User notes from Excel review.</summary>
    [ObservableProperty]
    private string _notes = string.Empty;
}

/// <summary>
/// 3D point for entity positioning.
/// </summary>
public partial struct Point3d
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

/// <summary>
/// Translation status for tracking entity processing state.
/// </summary>
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