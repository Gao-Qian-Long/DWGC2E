using ACadSharp.Entities;
using Serilog;

using CadEntity = ACadSharp.Entities.Entity;
using CadInsert = ACadSharp.Entities.Insert;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Shared geometry utilities for bounding-box computation, AABB transforms,
/// and recursive block-insert walking. Eliminates duplication between
/// <see cref="DwgCollisionDetector"/> and <see cref="DwgFrameDetector"/>.
/// </summary>
internal static class CadGeometryHelper
{
    /// <summary>
    /// Transforms an axis-aligned bounding box through a scale → rotate → translate pipeline.
    /// Computes the new AABB that encloses all 4 transformed corner points.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY) TransformAabb(
        double minX, double minY, double maxX, double maxY,
        double scaleX, double scaleY, double cosR, double sinR,
        double translateX, double translateY)
    {
        (double x, double y) Transform(double bx, double by)
        {
            double tx = bx * scaleX;
            double ty = by * scaleY;
            return (tx * cosR - ty * sinR + translateX,
                    tx * sinR + ty * cosR + translateY);
        }

        var c1 = Transform(minX, minY);
        var c2 = Transform(maxX, maxY);
        var c3 = Transform(minX, maxY);
        var c4 = Transform(maxX, minY);

        return (
            Math.Min(Math.Min(c1.x, c2.x), Math.Min(c3.x, c4.x)),
            Math.Min(Math.Min(c1.y, c2.y), Math.Min(c3.y, c4.y)),
            Math.Max(Math.Max(c1.x, c2.x), Math.Max(c3.x, c4.x)),
            Math.Max(Math.Max(c1.y, c2.y), Math.Max(c3.y, c4.y)));
    }

    /// <summary>
    /// Computes an axis-aligned bounding box from a collection of 2D points.
    /// Returns null if the collection is empty.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY)? ComputeAabbFromPoints(
        IEnumerable<(double X, double Y)> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var (x, y) in points)
        {
            any = true;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return any ? (minX, minY, maxX, maxY) : null;
    }

    /// <summary>
    /// Recursively estimates the combined bounding box of all entities in a block INSERT,
    /// applying scale, rotation, and translation transforms at each recursion level.
    /// Limited to <paramref name="maxDepth"/> to prevent stack overflow from circular references.
    /// Returns null if the block is empty or all entities are unsupported.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY)? EstimateInsertBounds(
        CadInsert insert,
        Func<CadEntity, int, (double minX, double minY, double maxX, double maxY)?> getEntityBounds,
        int depth = 0,
        int maxDepth = 5)
    {
        if (depth > maxDepth) return null;

        try
        {
            var blockDef = insert.Block;
            if (blockDef?.Entities == null || blockDef.Entities.Count == 0)
            {
                double fallbackW = Math.Abs(insert.XScale) * 100;
                double fallbackH = Math.Abs(insert.YScale) * 100;
                return (insert.InsertPoint.X, insert.InsertPoint.Y,
                        insert.InsertPoint.X + fallbackW, insert.InsertPoint.Y + fallbackH);
            }

            // Compute the AABB of all entities in the block
            double? gMinX = null, gMinY = null, gMaxX = null, gMaxY = null;
            foreach (var blkEntity in blockDef.Entities)
            {
                var b = getEntityBounds(blkEntity, depth);
                if (!b.HasValue) continue;
                if (!gMinX.HasValue || b.Value.minX < gMinX) gMinX = b.Value.minX;
                if (!gMinY.HasValue || b.Value.minY < gMinY) gMinY = b.Value.minY;
                if (!gMaxX.HasValue || b.Value.maxX > gMaxX) gMaxX = b.Value.maxX;
                if (!gMaxY.HasValue || b.Value.maxY > gMaxY) gMaxY = b.Value.maxY;
            }

            if (!gMinX.HasValue)
            {
                double fw = Math.Abs(insert.XScale) * 100;
                double fh = Math.Abs(insert.YScale) * 100;
                return (insert.InsertPoint.X, insert.InsertPoint.Y,
                        insert.InsertPoint.X + fw, insert.InsertPoint.Y + fh);
            }

            // Apply the insert transform to the block bounds
            double cosR = Math.Cos(insert.Rotation);
            double sinR = Math.Sin(insert.Rotation);

            return TransformAabb(
                gMinX!.Value, gMinY!.Value, gMaxX!.Value, gMaxY!.Value,
                insert.XScale, insert.YScale, cosR, sinR,
                insert.InsertPoint.X, insert.InsertPoint.Y);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to estimate insert bounds for block '{Name}'", insert.Block?.Name);
            return null;
        }
    }
}
