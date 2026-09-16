using ACadSharp.Entities;
using DwgTranslator.Core.Models;
using Serilog;

using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Pre-layout adjustments before collision resolution.
/// New policy: preserve original height whenever possible.
/// Only apply gentle width wrapping for MText; do NOT pre-shrink height
/// (height reduction is owned by DwgCollisionDetector after wrap/nudge fail).
/// </summary>
internal static class DwgTextScaler
{
    private static readonly string[] MTextLineSeparator = ["\\P"];

    /// <summary>
    /// DBText: keep original height. No pre-scale.
    /// Width overflow is handled later by collision wrap/nudge/scale pipeline.
    /// </summary>
    public static void ApplyScaling(CadText textEntity, string translatedText, CoreTextEntity ourEntity)
    {
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : textEntity.Height;
        if (originalHeight > 0)
            textEntity.Height = originalHeight;

        // Never grow height
        if (originalHeight > 0 && textEntity.Height > originalHeight * WritebackConstants.HeightCapRatio)
            textEntity.Height = originalHeight * WritebackConstants.HeightCapRatio;
    }

    /// <summary>
    /// MText pre-layout:
    /// 1. Force original height
    /// 2. Preserve line spacing
    /// 3. Prefer wrap width adjustments (never Width=0 for fixed-width originals)
    /// 4. Do NOT shrink height here
    /// </summary>
    public static void ApplyScaling(CadMText mtext, string translatedText, CoreTextEntity ourEntity)
    {
        if (string.IsNullOrEmpty(translatedText)) return;

        try
        {
            double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.Height;
            double originalWidth = ourEntity.OriginalWidth;
            double originalRectWidth = ourEntity.MTextRectangleWidth;

            // Always restore original height first
            if (originalHeight > 0)
                mtext.Height = originalHeight;

            if (ourEntity.MTextLineSpacing > 0)
                mtext.LineSpacing = ourEntity.MTextLineSpacing;
            if (ourEntity.MTextLineSpacingStyle > 0)
                mtext.LineSpacingStyle = (LineSpacingStyleType)ourEntity.MTextLineSpacingStyle;

            if (mtext.HasColumns && mtext.ColumnData != null)
                mtext.ColumnData.ColumnType = ColumnType.NoColumns;

            var logicalLines = SplitMTextLines(translatedText);
            int lineCount = Math.Max(1, logicalLines.Count);

            double maxLineWidth = 0;
            foreach (var line in logicalLines)
            {
                double lineW = TextWidthEstimator.EstimateTextWidth(line, mtext.Height);
                if (lineW > maxLineWidth) maxLineWidth = lineW;
            }

            string flat = translatedText.Replace("\\P", " ");
            double concatenatedWidth = TextWidthEstimator.EstimateTextWidth(flat, mtext.Height);
            double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;

            if (originalRectWidth > 0)
            {
                // Fixed-width source: keep a non-zero wrap width near original.
                // Slightly expand if translation is longer; do not collapse.
                double widthRatio = effectiveWidth / Math.Max(originalRectWidth, 1.0);
                double target;
                if (widthRatio <= 1.0)
                {
                    // Content fits or narrower: keep original rect (stable layout)
                    target = originalRectWidth;
                }
                else
                {
                    // Content wider: modest expand, still wrap-friendly
                    target = Math.Min(originalRectWidth * 1.20, Math.Max(originalRectWidth, effectiveWidth * 0.95));
                }

                mtext.RectangleWidth = Math.Max(target, WritebackConstants.MinMTextRectangleWidth);
            }
            else
            {
                // Free-width source: only introduce wrap width when translation is
                // dramatically longer; keep height untouched.
                if (originalWidth > 0 && effectiveWidth > originalWidth * WritebackConstants.PreScaleWidthOverflowRatio)
                {
                    double target = Math.Max(originalWidth * 1.05, effectiveWidth * 0.80);
                    mtext.RectangleWidth = Math.Max(target, WritebackConstants.MinMTextRectangleWidth);
                }
            }

            // Height cap only (no shrink)
            if (originalHeight > 0 && mtext.Height > originalHeight * WritebackConstants.HeightCapRatio)
                mtext.Height = originalHeight * WritebackConstants.HeightCapRatio;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MText pre-layout failed for entity {Handle}", mtext.Handle);
        }
    }

    public static List<string> SplitMTextLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        return [.. text.Split(MTextLineSeparator, StringSplitOptions.None)];
    }
}
