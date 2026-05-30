using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Serilog;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Optimizes MText layout using AutoCAD's precise GeometricExtents.
/// Employs binary search to find the largest height that fits within the frame.
/// </summary>
public static class LayoutOptimizer
{
    private const int MaxBinarySearchIterations = 8;
    private const double MinHeightRatio = 0.35;

    /// <summary>
    /// Optimizes MText layout:
    /// 1. Preserves original line spacing.
    /// 2. FIXED-RECTANGLE MText: never expands width (designer already set it).
    ///    Only scales down height when translated text needs more lines.
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

        // Step 1: Preserve original line spacing
        if (ourEntity.MTextLineSpacing > 0)
            mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
        if (ourEntity.MTextLineSpacingStyle > 0)
            mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

        // Step 2: Handle rectangle width
        double originalRectWidth = ourEntity.MTextRectangleWidth;
        string textForEstimation = translatedText.Replace("\\P", " ");
        double translatedWidth = EstimateTextWidth(textForEstimation, mtext.TextHeight);

        if (originalRectWidth > 0)
        {
            // FIXED rectangle width: adapt to translated text width to prevent sparse word spacing.
            // The original width was tuned for the source language; after translation the text
            // may be much narrower (CN→EN) or wider (EN→CN), causing AutoCAD to stretch gaps.
            double widthRatio = translatedWidth / originalRectWidth;

            if (widthRatio < 0.55)
            {
                // Translated text is much narrower than the fixed rectangle.
                // Shrink rectangle to fit the text with a small margin, preventing
                // AutoCAD from distributing words across excessive whitespace.
                double targetWidth = Math.Max(translatedWidth * 1.20, originalRectWidth * 0.45);
                mtext.Width = targetWidth;
            }
            else if (widthRatio < 0.75)
            {
                // Moderately narrower — reduce width proportionally
                double targetWidth = Math.Max(translatedWidth * 1.12, originalRectWidth * 0.6);
                mtext.Width = targetWidth;
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

        // Step 3: If frame detected, use binary search for optimal height
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
        Log.Debug("MText {Handle} optimized: height {H:F2} fits frame", mtext.Handle, bestHeight);
    }

    private static void HeuristicScaling(MText mtext, string translatedText, Core.Models.TextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.TextHeight;

        double newWidth = EstimateTextWidth(translatedText, mtext.TextHeight);
        if (newWidth > originalWidth)
        {
            double scale = originalWidth / newWidth;
            if (scale < 0.6) scale = 0.6;
            double newHeight = originalHeight * scale;
            if (newHeight < mtext.TextHeight)
                mtext.TextHeight = newHeight;
        }
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
    }

    private static double EstimateTextWidth(string text, double height) => Core.Services.TextWidthEstimator.EstimateTextWidth(text, height);

    private static int EstimateLineCount(string text, double height, double rectWidth) => Core.Services.TextWidthEstimator.EstimateLineCount(text, height, rectWidth);
}
