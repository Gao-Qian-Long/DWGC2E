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
            // FIXED rectangle width: keep original — designer already tuned it.
            mtext.Width = originalRectWidth;
        }
        else if (mtext.Width <= 0 && originalWidth > 0)
        {
            // FREE width: set a conservative rectangle width.
            if (translatedWidth > originalWidth * 1.05)
            {
                double targetWidth = Math.Min(originalWidth * 1.2,
                    Math.Max(originalWidth, translatedWidth * 0.55));
                mtext.Width = targetWidth;
            }
            else if (translatedWidth > 0)
            {
                mtext.Width = Math.Min(translatedWidth * 1.02, originalWidth * 1.2);
            }
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

    private static double EstimateTextWidth(string text, double height)
    {
        if (string.IsNullOrEmpty(text) || height <= 0) return 0;
        double width = 0;
        foreach (char c in text)
        {
            if (c == ' ') width += height * 0.20;
            else if (c >= 0x4E00 && c <= 0x9FFF) width += height * 1.0;
            else if (c >= 0x3000 && c <= 0x303F) width += height * 1.0;
            else if (c >= 0xFF00 && c <= 0xFFEF) width += height * 1.0;
            else if (c >= 0x3040 && c <= 0x309F) width += height * 1.0;
            else if (c >= 0x30A0 && c <= 0x30FF) width += height * 1.0;
            else if (char.IsUpper(c)) width += height * 0.55;
            else if (char.IsLower(c)) width += height * 0.45;
            else if (char.IsDigit(c)) width += height * 0.50;
            else width += height * 0.45;
        }
        return width;
    }

    private static int EstimateLineCount(string text, double height, double rectWidth)
    {
        if (rectWidth <= 0 || string.IsNullOrEmpty(text) || height <= 0) return 1;
        var hardLines = text.Split(new[] { "\\P", "\n", "\r\n" }, StringSplitOptions.None);
        int totalLines = 0;
        foreach (var line in hardLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                totalLines++;
                continue;
            }
            double lineWidth = EstimateTextWidth(line, height);
            totalLines += Math.Max(1, (int)Math.Ceiling(lineWidth / rectWidth));
        }
        return Math.Max(1, totalLines);
    }
}
