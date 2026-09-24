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
    /// Preferred minimum text height ratio when fitting a translation back into the
    /// space measured for the original text. A fit at or above this is "clean".
    /// </summary>
    public const double PreferredFitHeightRatio = 0.70;

    /// <summary>
    /// Absolute minimum text height ratio accepted by the envelope fit before the
    /// translation is given up and the original text is kept.
    ///
    /// Below <see cref="PreferredFitHeightRatio"/> the fit is still accepted (so the
    /// drawing ends up translated instead of silently reverting to the source
    /// language) but it is reported as a shrunk fit. Rotated labels are the common
    /// case: their available-space measurement degrades to the original bounding
    /// box, so longer translations can only be made to fit by shrinking further.
    /// </summary>
    public const double AbsoluteMinFitHeightRatio = 0.40;

    /// <summary>
    /// Collision margin as a ratio of original text height for proximity search.
    /// Smaller values reduce false positives from nearby non-touching geometry.
    /// </summary>
    public const double CollisionMarginRatio = 0.15;

    /// <summary>
    /// Visible clearance between translated glyph ink and non-text geometry such as
    /// title-block cell borders. The absolute floor also covers very small text.
    /// </summary>
    public const double GeometryClearanceRatio = 0.04;

    public const double MinGeometryClearance = 0.02;

    public static double GeometryClearance(double textHeight) =>
        Math.Max(Math.Abs(textHeight) * GeometryClearanceRatio, MinGeometryClearance);

    /// <summary>
    /// Preferred minimum visible gap between separately rendered text objects. A gap around
    /// two-fifths of the text height gives adjacent labels and values room to breathe after
    /// translations expand into the source drawing's whitespace.
    /// </summary>
    public const double InterTextClearanceRatio = 0.40;

    public const double MinInterTextClearance = 0.05;

    public static double InterTextClearance(double textHeight) =>
        Math.Max(Math.Abs(textHeight) * InterTextClearanceRatio, MinInterTextClearance);

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
    /// Condensation floor for the last-resort DBText tier.
    ///
    /// The DBText fit first searches a uniform scale, which preserves the glyph aspect ratio and is
    /// therefore the preferred answer ("as large as the cell allows, not distorted"). Only when no
    /// uniform scale fits does it condense the glyphs, searching for the largest width factor that
    /// fits and never going below this fraction of the source width factor. 0.40 matches the
    /// historical behaviour; reaching it means the cell is genuinely too small, and the original
    /// text is kept only if even this fails.
    /// </summary>
    public const double FallbackWidthFactorRetention = 0.40;

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
