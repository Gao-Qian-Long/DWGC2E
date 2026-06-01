using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Optimizes MText layout using AutoCAD's precise GeometricExtents.
///
/// Width rules (IMPROVED — NEVER set Width=0 for fixed-width MText):
///   AutoCAD official docs: "If Width = 0.0, word wrap is currently disabled."
///   Setting Width=0 disables all word wrapping, turning multi-line text into
///   a single very long line. We now use a tight non-zero width instead.
/// - Fixed width MText: if narrow (<0.75) → tight width (never 0); if medium (<0.95) → proportional; else → keep
/// - Free width MText: only constrain if overflowing (>1.2x original)
///
/// Height rules:
/// - Proportional scaling when translated lines exceed original line count
/// - Cap at originalHeight * 1.05 (collision avoidance, matches offline)
/// - Frame overflow: iterative height reduction + width constraint
/// </summary>
public static class LayoutOptimizer
{
    private const double MinHeightRatio = 0.80;  // 80% floor — text readability first

    public static void OptimizeMText(
        MText mtext,
        string translatedText,
        Core.Models.TextEntity ourEntity,
        Extents3d? frame,
        Transaction? tr)
    {
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.TextHeight;
        double originalWidth = ourEntity.OriginalWidth;
        double originalLineSpacing = ourEntity.MTextLineSpacing > 0 ? ourEntity.MTextLineSpacing : 1.0;
        double originalRectWidth = ourEntity.MTextRectangleWidth;

        // Preserve original line spacing
        if (ourEntity.MTextLineSpacing > 0)
            mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
        if (ourEntity.MTextLineSpacingStyle > 0)
            mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

        // Determine analysis text — prefer mtext.Contents with \P breaks
        string analysisText;
        if (mtext.Contents.Contains("\\P"))
            analysisText = mtext.Contents;
        else if (translatedText.Contains("\\P"))
            analysisText = translatedText;
        else if (ourEntity.MTextHasHardBreaks)
            analysisText = Core.Services.TextWidthEstimator.RebuildMTextWithLineBreaks(
                translatedText, ourEntity.MTextLineCount);
        else
            analysisText = translatedText;

        var logicalLines = SplitMTextLines(analysisText);
        int lineCount = logicalLines.Count;

        // Per-line max width (for multi-line) and concatenated width (for single-line)
        double maxLineWidth = 0;
        foreach (var line in logicalLines)
        {
            double lineW = EstimateTextWidth(line, mtext.TextHeight);
            if (lineW > maxLineWidth) maxLineWidth = lineW;
        }
        string textForEstimation = analysisText.Replace("\\P", " ");
        double concatenatedWidth = EstimateTextWidth(textForEstimation, mtext.TextHeight);
        double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;

        // ===== WIDTH: scale proportionally for fixed-width MText =====
        if (originalRectWidth > 0)
        {
            // Adjust rectangle width proportionally to match translated text.
            // Clamp between 60% and 150% of original to avoid extremes
            // while still adapting to the new language's width.
            double widthRatio = effectiveWidth / Math.Max(originalWidth, 1.0);
            double clampedRatio = Math.Clamp(widthRatio, 0.40, 1.20);
            double targetWidth = originalRectWidth * clampedRatio;
            if (targetWidth < 10.0) targetWidth = 10.0;  // minimum usable width
            mtext.Width = targetWidth;
            Log.Information("MText {Handle}: scaled width {OrigW:F1}→{NewW:F1} (ratio={R:F2})",
                mtext.Handle, originalRectWidth, targetWidth, clampedRatio);
        }
        else
        {
            // FREE width (Width=0): set a wrapping width only when the
            // translation is significantly wider than the original to avoid
            // excessively long single lines.
            if (effectiveWidth > Math.Max(originalWidth * 1.05, 50.0))
            {
                double targetWidth = Math.Max(originalWidth * 1.1, effectiveWidth * 0.55);
                mtext.Width = targetWidth;
                Log.Information("MText {Handle}: free-width now {W:F1} to wrap (orig={Orig:F1}, eff={Eff:F1})",
                    mtext.Handle, targetWidth, originalWidth, effectiveWidth);
            }
        }

        mtext.RecordGraphicsModified(true);

        // ===== HEIGHT: proportional scaling when line count increases =====
        double currentRectWidth = mtext.Width > 0 ? mtext.Width : (lineCount > 1 ? maxLineWidth : effectiveWidth * 1.1);
        if (currentRectWidth <= 0) currentRectWidth = originalWidth;

        var originalLogicalLines = SplitMTextLines(ourEntity.RawText ?? string.Empty);
        int originalLineCount = Math.Max(1, originalLogicalLines.Count);

        int estimatedLines = 0;
        foreach (var line in logicalLines)
            estimatedLines += EstimateLineCount(line, mtext.TextHeight, currentRectWidth);
        estimatedLines = Math.Max(1, estimatedLines);

        if (estimatedLines > originalLineCount)
        {
            double originalTotalHeight = originalLineCount * originalHeight * originalLineSpacing;
            double translatedTotalHeight = estimatedLines * mtext.TextHeight * originalLineSpacing;

            if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
            {
                double scale = originalTotalHeight / translatedTotalHeight;
                double newHeight = mtext.TextHeight * scale;
                double minHeight = originalHeight * MinHeightRatio;
                if (newHeight < minHeight) newHeight = minHeight;
                if (newHeight < mtext.TextHeight)
                    mtext.TextHeight = newHeight;
            }
        }

        // ===== FRAME SAFETY: simple height adjustment (no recursion) =====
        if (frame.HasValue)
        {
            AdjustHeightToFrame(mtext, frame.Value, originalHeight);
        }

        // ===== HEIGHT CAP: collision avoidance (matches offline) =====
        // Never let height grow beyond original + 5% — translated text is
        // usually longer (more chars), not taller.
        if (mtext.TextHeight > originalHeight * 1.05)
        {
            mtext.TextHeight = originalHeight * 1.05;
        }

        mtext.RecordGraphicsModified(true);
    }

    /// <summary>
    /// Simple frame fitting: reduce height in 2 passes (coarse then fine).
    /// If text overflows horizontally (Width=0 + long lines), constrain width to frame.
    /// </summary>
    private static void AdjustHeightToFrame(MText mtext, Extents3d frame, double originalHeight)
    {
        double frameWidth = frame.MaxPoint.X - frame.MinPoint.X;
        double minHeight = originalHeight * MinHeightRatio;

        // Pass 1: check if text fits at current height
        try
        {
            var bounds = mtext.GeometricExtents;
            if (!CollisionDetector.ExceedsFrame(bounds, frame))
                return; // fits — done
        }
        catch { /* GeometricExtents may fail; continue to adjustment */ }

        // Pass 2: try reducing height to fit
        double testHeight = mtext.TextHeight;
        for (int i = 0; i < 3; i++)
        {
            testHeight = Math.Max(testHeight * 0.85, minHeight);
            mtext.TextHeight = testHeight;
            mtext.RecordGraphicsModified(true);

            try
            {
                var testBounds = mtext.GeometricExtents;
                if (!CollisionDetector.ExceedsFrame(testBounds, frame))
                {
                    Log.Debug("MText {Handle}: height {H:F2} fits frame", mtext.Handle, testHeight);
                    return; // fits — done
                }
            }
            catch { break; /* stop on error */ }
        }

        // If height reduction didn't help, horizontal overflow is the problem.
        // Constrain width to frame width so AutoCAD auto-wraps.
        if (mtext.Width <= 0 || mtext.Width > frameWidth)
        {
            mtext.Width = frameWidth * 0.92;
            mtext.RecordGraphicsModified(true);
            Log.Debug("MText {Handle}: width constrained to {W:F1} (frame={Fw:F1})",
                mtext.Handle, mtext.Width, frameWidth);
        }
    }

    public static void OptimizeDBText(
        DBText dbText,
        string translatedText,
        Core.Models.TextEntity ourEntity,
        Extents3d? frame = null)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : dbText.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        double newWidth = EstimateTextWidth(translatedText, dbText.Height);
        if (newWidth > originalWidth)
        {
            double scale = originalWidth / newWidth;
            if (scale < 0.80) scale = 0.80;  // Consistent with MText MinHeightRatio
            dbText.Height = originalHeight * scale;
        }

        dbText.RecordGraphicsModified(true);

        // Frame overflow detection: if text exceeds frame, scale down to fit
        if (frame.HasValue)
        {
            try
            {
                dbText.RecordGraphicsModified(true);
                var bounds = dbText.GeometricExtents;
                if (CollisionDetector.ExceedsFrame(bounds, frame.Value))
                {
                    double overflowRatio = CollisionDetector.ComputeOverflowRatio(bounds, frame.Value);
                    double scale = 1.0 / (1.0 + overflowRatio);
                    if (scale < 0.5) scale = 0.5;
                    dbText.Height *= scale;
                    dbText.RecordGraphicsModified(true);

                    // Verify and do a second pass if needed
                    try
                    {
                        var newBounds = dbText.GeometricExtents;
                        if (CollisionDetector.ExceedsFrame(newBounds, frame.Value))
                        {
                            double ratio2 = CollisionDetector.ComputeOverflowRatio(newBounds, frame.Value);
                            double scale2 = 1.0 / (1.0 + ratio2);
                            if (scale2 < 0.5) scale2 = 0.5;
                            dbText.Height *= scale2;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static double EstimateTextWidth(string text, double height) =>
        Core.Services.TextWidthEstimator.EstimateTextWidth(text, height);

    private static List<string> SplitMTextLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string> { string.Empty };
        return text.Split(new[] { "\\P" }, StringSplitOptions.None).ToList();
    }

    private static int EstimateLineCount(string text, double height, double rectWidth) =>
        Core.Services.TextWidthEstimator.EstimateLineCount(text, height, rectWidth);
}
