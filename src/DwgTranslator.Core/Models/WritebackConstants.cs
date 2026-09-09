namespace DwgTranslator.Core.Models;

/// <summary>
/// Shared constants for the DWG writeback collision/layout pipeline.
/// Used by both offline (ACadSharp) and online (AutoCAD .NET) engines.
/// </summary>
public static class WritebackConstants
{
    /// <summary>
    /// Minimum text height ratio relative to the original height.
    /// Keep high so collision resolution does not make text unreadable.
    /// </summary>
    public const double MinHeightRatio = 0.85;

    /// <summary>
    /// Soft floor used only after wrap + move have both failed.
    /// Never go below this even if residual overlap remains.
    /// </summary>
    public const double HardMinHeightRatio = 0.75;

    /// <summary>
    /// Collision margin as a ratio of original text height for proximity search.
    /// Smaller values reduce false positives from nearby non-touching geometry.
    /// </summary>
    public const double CollisionMarginRatio = 0.15;

    /// <summary>
    /// Binary-search iterations for height reduction.
    /// </summary>
    public const int MaxBinarySearchIterations = 16;

    /// <summary>
    /// Maximum text height growth after writeback.
    /// </summary>
    public const double HeightCapRatio = 1.0;

    /// <summary>
    /// Frame tolerance as a ratio of frame dimension.
    /// </summary>
    public const double FrameToleranceRatio = 0.005;

    /// <summary>
    /// Absolute minimum MText rectangle width (drawing units).
    /// Width=0 disables AutoCAD word wrap.
    /// </summary>
    public const double MinMTextRectangleWidth = 8.0;

    /// <summary>
    /// Minimum scale when fitting into a frame boundary.
    /// </summary>
    public const double MinFrameScale = 0.75;

    /// <summary>
    /// Headroom applied to content-based MText width estimates.
    /// </summary>
    public const double ContentWidthHeadroom = 1.15;

    /// <summary>
    /// Max position nudge as a ratio of original text height.
    /// </summary>
    public const double MaxNudgeRatio = 1.25;

    /// <summary>
    /// Number of position-nudge attempts (cardinal + diagonal directions).
    /// </summary>
    public const int MaxNudgeAttempts = 12;

    /// <summary>
    /// Pre-collision width overflow ratio before any height pre-scale is considered.
    /// Below this, prefer wrap/move over shrinking.
    /// </summary>
    public const double PreScaleWidthOverflowRatio = 1.75;
}
