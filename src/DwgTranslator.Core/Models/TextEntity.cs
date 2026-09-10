using CommunityToolkit.Mvvm.ComponentModel;

namespace DwgTranslator.Core.Models;

/// <summary>
/// Represents a text entity extracted from a DWG file.
/// Observable for WPF MVVM data binding.
/// </summary>
// TODO(architecture): TextEntity inherits from CommunityToolkit.Mvvm.ObservableObject,
// which is a UI framework dependency. In a clean architecture this Core model should be
// POCO-only, with the observable wrapper living in the UI/Presentation layer.
// Deferred because changing the base class would break data-binding across the entire app.
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
    [NotifyPropertyChangedFor(nameof(EntityTypeText))]
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

    /// <summary>Original text height recorded at extraction time.</summary>
    [ObservableProperty]
    private double _originalHeight;

    // ---- MText-specific layout properties (preserved from source for accurate writeback) ----

    /// <summary>Original MText rectangle width (0 = free-width).</summary>
    [ObservableProperty]
    private double _mTextRectangleWidth;

    /// <summary>Original MText line-spacing factor.</summary>
    [ObservableProperty]
    private double _mTextLineSpacing = 1.0;

    /// <summary>Original MText line-spacing style: 1=AtLeast, 2=Exact.</summary>
    [ObservableProperty]
    private int _mTextLineSpacingStyle = 1;

    /// <summary>Original line count inferred from \P breaks in RawText.</summary>
    [ObservableProperty]
    private int _mTextLineCount = 1;

    /// <summary>Whether the original MText had explicit \P line breaks.</summary>
    [ObservableProperty]
    private bool _mTextHasHardBreaks;

    /// <summary>Source DWG/DXF file path (used to scope multi-file import writeback).</summary>
    [ObservableProperty]
    private string _sourceFilePath = string.Empty;

    /// <summary>Translation status.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
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

    /// <summary>
    /// Status as shown in the grid. The raw enum name used to reach the interface, so a Chinese UI
    /// displayed "Pending" / "TranslationFailed" in the status column.
    /// </summary>
    public string StatusText => Status switch
    {
        TranslationStatus.Pending => "待翻译",
        TranslationStatus.GlossaryMatched => "术语命中",
        TranslationStatus.Translated => "已翻译",
        TranslationStatus.TranslationFailed => "翻译失败",
        TranslationStatus.Reviewed => "已审阅",
        TranslationStatus.WritebackSuccess => "已写回",
        TranslationStatus.WritebackFailed => "写回失败",
        TranslationStatus.Skipped => "已跳过",
        _ => Status.ToString()
    };

    /// <summary>Entity type as a short label for the grid, instead of the CLR type name.</summary>
    public string EntityTypeText => EntityType switch
    {
        "DBText" => "单行文字",
        "MText" => "多行文字",
        "AttributeReference" => "属性参照",
        "AttributeDefinition" => "属性定义",
        "Dimension" => "标注",
        "MLeader" => "多重引线",
        "Table" => "表格",
        "ArcAlignedText" => "弧线文字",
        "" => "-",
        _ => EntityType
    };
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