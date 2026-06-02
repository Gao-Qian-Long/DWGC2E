using ACadSharp.Entities;
using ACadSharp;

using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Estimates axis-aligned bounding boxes for CAD text entities.
/// Shared by frame detection and collision detection modules.
/// </summary>
internal static class DwgBoundsEstimator
{
    /// <summary>
    /// Estimates the bounding box of a CadText entity based on its insertion point,
    /// text width, and height. The insertion point is at the left baseline.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY) EstimateTextBounds(CadText textEntity)
    {
        double w = TextWidthEstimator.EstimateTextWidth(textEntity.Value ?? string.Empty, textEntity.Height);
        double h = textEntity.Height;
        double x = textEntity.InsertPoint.X;
        double y = textEntity.InsertPoint.Y;
        // Text extends right from insert point, and roughly from y to y+height
        return (x, y, x + w, y + h);
    }

    /// <summary>
    /// Estimates the bounding box of a CadMText entity based on its insertion point,
    /// attachment point, rectangle width, and estimated height.
    /// Correctly handles ALL 9 AutoCAD attachment point types.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY) EstimateMTextBounds(CadMText mtext)
    {
        double w;
        if (mtext.RectangleWidth > 0)
        {
            w = mtext.RectangleWidth;
        }
        else
        {
            // Free-width: estimate from content
            string plain = mtext.Value?.Replace("\\P", " ") ?? "";
            w = TextWidthEstimator.EstimateTextWidth(plain, mtext.Height);
        }

        // Estimate total height from line count
        int lineCount = TextWidthEstimator.EstimateLineCount(mtext.Value ?? "", mtext.Height, Math.Max(w, 1.0));
        double lineSpacing = mtext.LineSpacing > 0 ? mtext.LineSpacing : 1.0;
        double totalHeight = lineCount * mtext.Height * lineSpacing;

        double x = mtext.InsertPoint.X;
        double y = mtext.InsertPoint.Y;

        // Compute bounds based on the 9 possible attachment points
        return mtext.AttachmentPoint switch
        {
            // Top row: text extends DOWNWARD
            AttachmentPointType.TopLeft => (x, y - totalHeight, x + w, y),
            AttachmentPointType.TopCenter => (x - w / 2, y - totalHeight, x + w / 2, y),
            AttachmentPointType.TopRight => (x - w, y - totalHeight, x, y),

            // Middle row: text extends UPWARD and DOWNWARD
            AttachmentPointType.MiddleLeft => (x, y - totalHeight / 2, x + w, y + totalHeight / 2),
            AttachmentPointType.MiddleCenter => (x - w / 2, y - totalHeight / 2, x + w / 2, y + totalHeight / 2),
            AttachmentPointType.MiddleRight => (x - w, y - totalHeight / 2, x, y + totalHeight / 2),

            // Bottom row: text extends UPWARD
            AttachmentPointType.BottomLeft => (x, y, x + w, y + totalHeight),
            AttachmentPointType.BottomCenter => (x - w / 2, y, x + w / 2, y + totalHeight),
            AttachmentPointType.BottomRight => (x - w, y, x, y + totalHeight),

            _ => (x, y - totalHeight, x + w, y), // Default: TopLeft
        };
    }
}
