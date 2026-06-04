using ACadSharp.Entities;
using DwgTranslator.Core.Models;
using Serilog;

using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Auto-scales text height and MText rectangle width when translated text
/// has different dimensions than the original. Handles single-line text
/// height scaling and multi-line text adaptive layout.
/// </summary>
internal static class DwgTextScaler
{
    private static readonly string[] MTextLineSeparator = ["\\P"];

    /// <summary>
    /// Auto-scale text height when translated text is significantly wider than original.
    /// Uses improved per-character width estimation (CJK vs ASCII).
    /// Also applies a conservative cap to reduce collision risk with nearby geometry.
    /// </summary>
    public static void ApplyScaling(CadText textEntity, string translatedText, CoreTextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : textEntity.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        try
        {
            double newWidth = TextWidthEstimator.EstimateTextWidth(translatedText, textEntity.Height);
            if (newWidth > originalWidth)
            {
                double scale = originalWidth / newWidth;
                if (scale < 0.85) scale = 0.85; // keep at least 85% of original height
                double newHeight = originalHeight * scale;
                double oldHeight = textEntity.Height;
                textEntity.Height = newHeight;
                Log.Debug("Scaled text {Handle}: {OldH:F2} -> {NewH:F2}", textEntity.Handle, oldHeight, newHeight);
            }

            // Conservative safety cap: never allow height to grow beyond original
            // (translation usually makes text longer, not taller)
            if (originalHeight > 0 && textEntity.Height > originalHeight * 1.05)
            {
                textEntity.Height = originalHeight * 1.05;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scaling failed for text entity");
        }
    }

    /// <summary>
    /// Adaptive layout for MText:
    /// 1. Preserves original line spacing.
    /// 2. FIXED-RECTANGLE MText: shrinks rectangle to match actual text width,
    ///    preventing stretched word spacing in justified/fit modes.
    /// 3. FREE-WIDTH MText: sets a tight rectangle width that respects per-line
    ///    widths (not concatenated) to avoid over-wide sparse layout.
    /// 4. Conservative height cap: never exceeds original total height to reduce
    ///    collision risk with nearby geometry.
    /// </summary>
    public static void ApplyScaling(CadMText mtext, string translatedText, CoreTextEntity ourEntity)
    {
        if (string.IsNullOrEmpty(translatedText) || ourEntity.OriginalWidth <= 0) return;

        try
        {
            double originalWidth = ourEntity.OriginalWidth;
            double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.Height;
            double currentHeight = mtext.Height;
            double originalLineSpacing = ourEntity.MTextLineSpacing > 0 ? ourEntity.MTextLineSpacing : 1.0;

            // Preserve original line spacing from source drawing
            if (ourEntity.MTextLineSpacing > 0)
                mtext.LineSpacing = ourEntity.MTextLineSpacing;
            if (ourEntity.MTextLineSpacingStyle > 0)
                mtext.LineSpacingStyle = (LineSpacingStyleType)ourEntity.MTextLineSpacingStyle;

            // Split into logical lines (by \P) for per-line width estimation.
            var logicalLines = SplitMTextLines(translatedText);
            double maxLineWidth = 0;
            int lineCount = logicalLines.Count;
            foreach (var line in logicalLines)
            {
                double lineW = TextWidthEstimator.EstimateTextWidth(line, currentHeight);
                if (lineW > maxLineWidth) maxLineWidth = lineW;
            }

            // Also compute concatenated width for overflow detection
            string textForEstimation = translatedText.Replace("\\P", " ");
            double concatenatedWidth = TextWidthEstimator.EstimateTextWidth(textForEstimation, currentHeight);

            // Original line count for height budgeting
            var originalLogicalLines = SplitMTextLines(ourEntity.RawText ?? string.Empty);
            int originalLineCount = Math.Max(1, originalLogicalLines.Count);

            // Use per-line max width (not concatenated) for multi-line text
            double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;

            if (mtext.RectangleWidth > 0)
            {
                // FIXED rectangle width: the original was tuned for the source language.
                double originalRectWidth = mtext.RectangleWidth;
                double widthRatio = effectiveWidth / originalRectWidth;

                // KEY FIX: NEVER set RectangleWidth=0. AutoCAD DWG specification states:
                // "If Width=0.0, word wrap is currently disabled." Setting Width=0
                // causes multi-line text to render as a single unbroken line.
                if (widthRatio < 0.70)
                {
                    double contentWidth = maxLineWidth * 1.25;
                    mtext.RectangleWidth = Math.Clamp(contentWidth,
                        originalRectWidth * 0.50,
                        originalRectWidth * 0.95);
                }
                else if (widthRatio < 0.95)
                {
                    double targetWidth = Math.Max(effectiveWidth * 1.08, originalRectWidth * 0.60);
                    mtext.RectangleWidth = targetWidth;
                }
                else
                {
                    double clampedRatio = Math.Clamp(widthRatio, 0.70, 1.30);
                    mtext.RectangleWidth = originalRectWidth * clampedRatio;
                }

                // Estimate line count in the effective rectangle width
                double rectWidth = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : effectiveWidth * 1.1;
                int estimatedLines = 0;
                foreach (var line in logicalLines)
                {
                    double lineW = TextWidthEstimator.EstimateTextWidth(line, currentHeight);
                    // Add 5% tolerance to Ceiling to prevent 1% overflow being counted as an extra line
                    estimatedLines += Math.Max(1, (int)Math.Ceiling(lineW / (rectWidth * 1.05)));
                }
                estimatedLines = Math.Max(1, estimatedLines);

                if (estimatedLines > originalLineCount)
                {
                    double originalTotalHeight = originalLineCount * originalHeight * originalLineSpacing;
                    double translatedTotalHeight = estimatedLines * currentHeight * originalLineSpacing;

                    if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
                    {
                        double scale = originalTotalHeight / translatedTotalHeight;
                        double newHeight = currentHeight * scale;
                        double minHeight = originalHeight * 0.85;
                        if (newHeight < minHeight) newHeight = minHeight;
                        if (newHeight < currentHeight)
                        {
                            mtext.Height = newHeight;
                            currentHeight = newHeight;
                        }
                    }
                }
            }
            else
            {
                // Original was FREE width: only set a rectangle when truly necessary.
                if (effectiveWidth > originalWidth * 1.3)
                {
                    double maxAllowable = originalWidth * 1.3;
                    double targetWidth = Math.Min(maxAllowable, Math.Max(originalWidth * 1.05, effectiveWidth * 0.80));
                    mtext.RectangleWidth = targetWidth;
                }
            }

            // Conservative collision-avoidance cap: never let height grow beyond
            // original height + 5%.
            if (originalHeight > 0 && mtext.Height > originalHeight * 1.05)
            {
                mtext.Height = originalHeight * 1.05;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MText scaling failed for entity {Handle}", mtext.Handle);
        }
    }

    /// <summary>
    /// Splits MText content into logical lines by \P (hard paragraph break).
    /// Empty lines are preserved as empty strings.
    /// </summary>
    public static List<string> SplitMTextLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        return [.. text.Split(MTextLineSeparator, StringSplitOptions.None)];
    }
}
