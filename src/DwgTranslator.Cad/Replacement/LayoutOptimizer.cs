using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Optimizes MText layout using AutoCAD's precise GeometricExtents.
/// Key improvements for translation quality:
/// 1. More aggressive width reduction for fixed-rectangle MText (prevents sparse word spacing)
/// 2. Width=0 (free-width) as primary strategy to let AutoCAD auto-size the rectangle
/// 3. Binary search for optimal height when frame is detected
/// </summary>
public static class LayoutOptimizer
{
    private const int MaxBinarySearchIterations = 8;
    private const double MinHeightRatio = 0.35;

    /// <summary>
    /// Optimizes MText layout:
    /// 1. Preserves original line spacing.
    /// 2. FIXED-RECTANGLE MText: tries free-width (Width=0) first, then reduces rectangle width aggressively.
    /// 3. FREE-WIDTH MText: sets a conservative rectangle width.
    /// 4. If a frame is present, uses binary search to find the optimal height that fits.
    /// </summary>
    public static void OptimizeMText(
        MText mtext,
        string translatedText,
        Core.Models.TextEntity ourEntity,
        Extents3d? frame,
        Transaction tr)
    {
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.TextHeight;
        double originalWidth = ourEntity.OriginalWidth;
        double originalLineSpacing = ourEntity.MTextLineSpacing > 0 ? ourEntity.MTextLineSpacing : 1.0;
        double originalRectWidth = ourEntity.MTextRectangleWidth;

        // Step 1: Preserve original line spacing
        if (ourEntity.MTextLineSpacing > 0)
            mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
        if (ourEntity.MTextLineSpacingStyle > 0)
            mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

        // Step 2: Force graphics update so GeometricExtents is accurate
        mtext.RecordGraphicsModified(true);

        // Step 3: Handle rectangle width
        string textForEstimation = translatedText.Replace("\\P", " ");
        double translatedWidth = EstimateTextWidth(textForEstimation, mtext.TextHeight);

        if (originalRectWidth > 0)
        {
            // FIXED rectangle width: the original was tuned for the source language.
            // After translation (especially CN→EN), text is usually narrower, causing
            // AutoCAD to stretch gaps between words to fill the fixed width.
            double widthRatio = translatedWidth / originalRectWidth;

            // Strategy A: Try free-width (Width=0) first — AutoCAD auto-sizes to content
            // This is the most effective fix for sparse word spacing.
            // We save the original width in case we need to revert.
            double savedWidth = mtext.Width;

            if (widthRatio < 0.85)
            {
                // Text is significantly narrower than the fixed rectangle.
                // Try free-width mode — let AutoCAD auto-size the rectangle.
                mtext.Width = 0;
                mtext.RecordGraphicsModified(true);

                try
                {
                    // Check if free-width result is acceptable
                    var testBounds = mtext.GeometricExtents;
                    double freeWidth = testBounds.MaxPoint.X - testBounds.MinPoint.X;
                    double freeHeight = testBounds.MaxPoint.Y - testBounds.MinPoint.Y;
                    double originalTotalHeight = EstimateLineCount(
                        (ourEntity.RawText ?? "").Replace("\\P", " "), originalHeight, originalRectWidth)
                        * originalHeight * originalLineSpacing;

                    // If free-width doesn't cause excessive height increase, keep it
                    if (freeHeight <= originalTotalHeight * 1.5 || widthRatio < 0.4)
                    {
                        Log.Debug("MText {Handle}: using free-width (Width=0), freeW={W:F1}, ratio={R:F2}",
                            mtext.Handle, freeWidth, widthRatio);
                        // Width=0 succeeded — continue to height optimization
                    }
                    else
                    {
                        // Free-width caused too many lines — fall back to reduced fixed width
                        double targetWidth = Math.Max(translatedWidth * 1.06, originalRectWidth * 0.4);
                        mtext.Width = Math.Min(targetWidth, originalRectWidth * 0.9);
                        mtext.RecordGraphicsModified(true);
                        Log.Debug("MText {Handle}: reduced width to {W:F1} (ratio={R:F2})",
                            mtext.Handle, mtext.Width, widthRatio);
                    }
                }
                catch
                {
                    // GeometricExtents failed — use heuristic reduced width
                    double targetWidth = Math.Max(translatedWidth * 1.06, originalRectWidth * 0.4);
                    mtext.Width = Math.Min(targetWidth, originalRectWidth * 0.9);
                    mtext.RecordGraphicsModified(true);
                }
            }
            else if (widthRatio < 0.95)
            {
                // Slightly narrower — small reduction
                double targetWidth = Math.Max(translatedWidth * 1.06, originalRectWidth * 0.7);
                mtext.Width = Math.Min(targetWidth, originalRectWidth);
                mtext.RecordGraphicsModified(true);
                Log.Debug("MText {Handle}: slightly reduced width to {W:F1} (ratio={R:F2})",
                    mtext.Handle, mtext.Width, widthRatio);
            }
            else
            {
                // Text roughly fills the rectangle — keep original fixed width
                mtext.Width = originalRectWidth;
            }
        }
        else if (mtext.Width <= 0 && originalWidth > 0)
        {
            // FREE width: only set rectangle width when truly necessary to prevent overflow.
            if (translatedWidth > originalWidth * 1.3)
            {
                double maxAllowable = originalWidth * 1.3;
                double preferred = translatedWidth * 0.65;
                double targetWidth = Math.Min(maxAllowable, Math.Max(originalWidth * 1.05, preferred));
                mtext.Width = targetWidth;
            }
            else if (translatedWidth > originalWidth * 1.05)
            {
                double targetWidth = Math.Min(translatedWidth * 1.05, originalWidth * 1.25);
                mtext.Width = targetWidth;
            }
            // else: keep Width = 0 (true free-width) for natural, tight spacing
        }

        // Step 4: If frame detected, use binary search for optimal height
        if (frame.HasValue)
        {
            BinarySearchOptimalHeight(mtext, frame.Value, originalHeight);
        }
        else
        {
            // No frame - scale down height if translated text needs more lines
            double rectWidth = mtext.Width > 0 ? mtext.Width : originalWidth;
            string originalText = (ourEntity.RawText ?? string.Empty).Replace("\\P", " ");
            int originalLines = EstimateLineCount(originalText, originalHeight, rectWidth);
            int translatedLines = EstimateLineCount(textForEstimation, mtext.TextHeight, rectWidth);

            if (translatedLines > originalLines)
            {
                double originalTotalHeight = originalLines * originalHeight * originalLineSpacing;
                double translatedTotalHeight = translatedLines * mtext.TextHeight * originalLineSpacing;

                if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
                {
                    double scale = originalTotalHeight / translatedTotalHeight;
                    double newHeight = mtext.TextHeight * scale;
                    double minHeight = originalHeight * 0.5;
                    if (newHeight < minHeight) newHeight = minHeight;
                    if (newHeight < mtext.TextHeight)
                        mtext.TextHeight = newHeight;
                }
            }
        }

        // Final graphics refresh
        mtext.RecordGraphicsModified(true);
    }

    private static void BinarySearchOptimalHeight(MText mtext, Extents3d frame, double originalHeight)
    {
        double minHeight = originalHeight * MinHeightRatio;
        double maxHeight = originalHeight;
        double bestHeight = originalHeight;

        for (int i = 0; i < MaxBinarySearchIterations; i++)
        {
            double midHeight = (minHeight + maxHeight) / 2.0;
            mtext.TextHeight = midHeight;
            mtext.RecordGraphicsModified(true);

            // Force boundary recalculation
            try
            {
                var bounds = mtext.GeometricExtents;
                bool overflows = CollisionDetector.ExceedsFrame(bounds, frame);

                if (overflows)
                {
                    maxHeight = midHeight;
                }
                else
                {
                    bestHeight = midHeight;
                    minHeight = midHeight;
                }
            }
            catch
            {
                // GeometricExtents may fail for degenerate text; treat as overflow
                maxHeight = midHeight;
            }
        }

        mtext.TextHeight = bestHeight;
        mtext.RecordGraphicsModified(true);
        Log.Debug("MText {Handle} optimized: height {H:F2} fits frame", mtext.Handle, bestHeight);
    }

    /// <summary>
    /// Optimizes DBText scaling when no frame is present or as a first-pass heuristic.
    /// </summary>
    public static void OptimizeDBText(
        DBText dbText,
        string translatedText,
        Core.Models.TextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : dbText.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        double newWidth = EstimateTextWidth(translatedText, dbText.Height);
        if (newWidth > originalWidth)
        {
            double scale = originalWidth / newWidth;
            if (scale < 0.6) scale = 0.6;
            dbText.Height = originalHeight * scale;
        }

        dbText.RecordGraphicsModified(true);
    }

    private static double EstimateTextWidth(string text, double height) => Core.Services.TextWidthEstimator.EstimateTextWidth(text, height);

    private static int EstimateLineCount(string text, double height, double rectWidth) => Core.Services.TextWidthEstimator.EstimateLineCount(text, height, rectWidth);
}
