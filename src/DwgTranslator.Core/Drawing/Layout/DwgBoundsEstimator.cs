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
    private const double RotationEpsilon = 0.001; // ~0.06 degrees

    /// <summary>
    /// Computes the axis-aligned bounding box of a rectangle defined by
    /// (rx, ry, rx+rw, ry+rh) after rotating it by the given rotation
    /// radians around (cx, cy).
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY) RotateAABB(
        double rx, double ry, double rw, double rh,
        double cx, double cy, double rotation)
    {
        double cos = Math.Cos(rotation);
        double sin = Math.Sin(rotation);

        // Four corners of the unrotated rectangle
        (double px, double py)[] corners =
        [
            (rx, ry),
            (rx + rw, ry),
            (rx + rw, ry + rh),
            (rx, ry + rh),
        ];

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var (px, py) in corners)
        {
            // Translate to origin, rotate, translate back
            double dx = px - cx;
            double dy = py - cy;
            double rotx = cx + dx * cos - dy * sin;
            double roty = cy + dx * sin + dy * cos;

            if (rotx < minX) minX = rotx;
            if (rotx > maxX) maxX = rotx;
            if (roty < minY) minY = roty;
            if (roty > maxY) maxY = roty;
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Estimates the bounding box of a CadText entity based on its insertion point,
    /// text width, and height. The insertion point is at the left baseline.
    /// Accounts for entity rotation.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY) EstimateTextBounds(CadText textEntity)
    {
        double w = TextWidthEstimator.EstimateTextWidth(textEntity.Value ?? string.Empty, textEntity.Height);
        double h = textEntity.Height;
        double x = textEntity.InsertPoint.X;
        double y = textEntity.InsertPoint.Y;

        double rotation = textEntity.Rotation;
        if (Math.Abs(rotation) < RotationEpsilon)
        {
            // Fast path: axis-aligned
            return (x, y, x + w, y + h);
        }

        return RotateAABB(x, y, w, h, x, y, rotation);
    }

    /// <summary>
    /// Estimates the bounding box of a CadMText entity based on its insertion point,
    /// attachment point, rectangle width, and estimated height.
    /// Correctly handles ALL 9 AutoCAD attachment point types.
    /// Accounts for entity rotation.
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
            // Free-width: estimate from content. Format codes (\f...\;, \H2x;\, %%c, ...)
            // are control sequences, not glyphs — strip them first or the width is
            // systematically overestimated.
            string plain = new DwgTranslator.Core.Translation.FormatCodeParser()
                .StripFormatCodes(mtext.Value ?? "")
                .Replace("\\P", " ");
            w = TextWidthEstimator.EstimateTextWidth(plain, mtext.Height);
        }

        // Estimate total height from line count
        int lineCount = TextWidthEstimator.EstimateLineCount(mtext.Value ?? "", mtext.Height, Math.Max(w, 1.0));
        double lineSpacing = mtext.LineSpacing > 0 ? mtext.LineSpacing : 1.0;
        double totalHeight = lineCount * mtext.Height * lineSpacing;

        double x = mtext.InsertPoint.X;
        double y = mtext.InsertPoint.Y;

        // Compute unrotated bounds based on the 9 possible attachment points
        (double rx, double ry, double rw, double rh) = mtext.AttachmentPoint switch
        {
            // Top row: text extends DOWNWARD
            AttachmentPointType.TopLeft => (x, y - totalHeight, w, totalHeight),
            AttachmentPointType.TopCenter => (x - w / 2, y - totalHeight, w, totalHeight),
            AttachmentPointType.TopRight => (x - w, y - totalHeight, w, totalHeight),

            // Middle row: text extends UPWARD and DOWNWARD
            AttachmentPointType.MiddleLeft => (x, y - totalHeight / 2, w, totalHeight),
            AttachmentPointType.MiddleCenter => (x - w / 2, y - totalHeight / 2, w, totalHeight),
            AttachmentPointType.MiddleRight => (x - w, y - totalHeight / 2, w, totalHeight),

            // Bottom row: text extends UPWARD
            AttachmentPointType.BottomLeft => (x, y, w, totalHeight),
            AttachmentPointType.BottomCenter => (x - w / 2, y, w, totalHeight),
            AttachmentPointType.BottomRight => (x - w, y, w, totalHeight),

            _ => (x, y - totalHeight, w, totalHeight), // Default: TopLeft
        };

        double rotation = mtext.Rotation;
        if (Math.Abs(rotation) < RotationEpsilon)
        {
            // Fast path: axis-aligned
            return (rx, ry, rx + rw, ry + rh);
        }

        return RotateAABB(rx, ry, rw, rh, x, y, rotation);
    }
}
