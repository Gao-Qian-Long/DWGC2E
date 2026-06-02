using ACadSharp.Entities;
using Serilog;

using CadEntity = ACadSharp.Entities.Entity;
using CadInsert = ACadSharp.Entities.Insert;
using CadLwPolyline = ACadSharp.Entities.LwPolyline;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Entity-level collision detection for DWG/DXF writeback.
/// After text replacement, checks whether the new text bounds overlap nearby
/// geometry, and uses binary search to scale text height down until no overlap
/// exists. Supports 10+ entity types for bounds estimation.
/// </summary>
internal static class DwgCollisionDetector
{
    /// <summary>
    /// Estimates the axis-aligned bounding box of any CAD entity for collision detection.
    /// Supports 10+ entity types: Text, MText, Insert, LwPolyline, Line, Arc, Circle, Spline.
    /// Depth parameter limits recursion into block inserts (max 5 levels).
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY)? GetEntityBounds(CadEntity entity, int depth = 0)
    {
        switch (entity)
        {
            case CadText text:
                return DwgBoundsEstimator.EstimateTextBounds(text);

            case CadMText mtext:
                return DwgBoundsEstimator.EstimateMTextBounds(mtext);

            case CadInsert insert:
                return EstimateInsertBounds(insert, depth + 1);

            case CadLwPolyline poly:
                if (poly.Vertices.Count > 0)
                {
                    double pminX = poly.Vertices.Min(v => v.Location.X);
                    double pminY = poly.Vertices.Min(v => v.Location.Y);
                    double pmaxX = poly.Vertices.Max(v => v.Location.X);
                    double pmaxY = poly.Vertices.Max(v => v.Location.Y);
                    return (pminX, pminY, pmaxX, pmaxY);
                }
                return null;

            case Line line:
                return (
                    Math.Min(line.StartPoint.X, line.EndPoint.X),
                    Math.Min(line.StartPoint.Y, line.EndPoint.Y),
                    Math.Max(line.StartPoint.X, line.EndPoint.X),
                    Math.Max(line.StartPoint.Y, line.EndPoint.Y));

            case Arc arc:
                // Arc MUST come before Circle — in ACadSharp, Arc extends Circle.
                return EstimateArcBounds(arc);

            case Circle circle:
                double r = circle.Radius;
                return (
                    circle.Center.X - r, circle.Center.Y - r,
                    circle.Center.X + r, circle.Center.Y + r);

            case Spline spline:
                // Use control points for a conservative bounding box.
                if (spline.ControlPoints.Count > 0)
                {
                    double sminX = spline.ControlPoints.Min(p => p.X);
                    double sminY = spline.ControlPoints.Min(p => p.Y);
                    double smaxX = spline.ControlPoints.Max(p => p.X);
                    double smaxY = spline.ControlPoints.Max(p => p.Y);
                    return (sminX, sminY, smaxX, smaxY);
                }
                return null;

            // Hatch, Solid, Polyline2D, Polyline3D — bounds are complex to compute
            // accurately in the offline path. Return null so ScaleDownToAvoidCollisions
            // can focus on known types.
            default:
                return null;
        }
    }

    /// <summary>
    /// Estimates the bounding box of a CadInsert by computing the transformed
    /// bounds of all entities in the referenced block definition.
    /// Limited to maxDepth=5 to prevent stack overflow from circular block references.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY)? EstimateInsertBounds(CadInsert insert, int depth = 0)
    {
        const int maxDepth = 5;
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

            // Compute the AABB of all entities in the block, then transform
            double? gMinX = null, gMinY = null, gMaxX = null, gMaxY = null;
            foreach (var blkEntity in blockDef.Entities)
            {
                var b = depth + 1 <= maxDepth ? GetEntityBounds(blkEntity) : null;
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
            double sx = insert.XScale;
            double sy = insert.YScale;
            double ix = insert.InsertPoint.X;
            double iy = insert.InsertPoint.Y;

            (double x, double y) Transform(double bx, double by)
            {
                double tx = bx * sx;
                double ty = by * sy;
                return (tx * cosR - ty * sinR + ix,
                        tx * sinR + ty * cosR + iy);
            }

            double minX = gMinX!.Value, minY = gMinY!.Value;
            double maxX = gMaxX!.Value, maxY = gMaxY!.Value;
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
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to estimate insert bounds for block '{Name}'", insert.Block?.Name);
            return null;
        }
    }

    /// <summary>
    /// Estimates the bounding box of an arc by sampling points at key angles
    /// (start, end, and the 4 quadrant boundaries: 0°, 90°, 180°, 270°).
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY) EstimateArcBounds(Arc arc)
    {
        double cx = arc.Center.X;
        double cy = arc.Center.Y;
        double r = arc.Radius;

        var angles = new List<double> { arc.StartAngle, arc.EndAngle };
        double sweep = arc.EndAngle - arc.StartAngle;
        if (sweep < 0) sweep += 2 * Math.PI;

        for (double a = 0; a < 2 * Math.PI; a += Math.PI / 2)
        {
            double da = a - arc.StartAngle;
            if (da < 0) da += 2 * Math.PI;
            if (da <= sweep)
                angles.Add(a);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (double a in angles)
        {
            double px = cx + r * Math.Cos(a);
            double py = cy + r * Math.Sin(a);
            if (px < minX) minX = px; if (px > maxX) maxX = px;
            if (py < minY) minY = py; if (py > maxY) maxY = py;
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Returns true if two axis-aligned bounding boxes overlap, with a configurable margin.
    /// </summary>
    public static bool HasBoundsOverlap(
        (double minX, double minY, double maxX, double maxY) a,
        (double minX, double minY, double maxX, double maxY) b,
        double margin = 2.0)
    {
        return a.minX - margin < b.maxX &&
               a.maxX + margin > b.minX &&
               a.minY - margin < b.maxY &&
               a.maxY + margin > b.minY;
    }

    /// <summary>
    /// Sets the Height property on text entities (CadText or CadMText).
    /// No-op for other entity types.
    /// </summary>
    private static void TrySetEntityHeight(CadEntity entity, double height)
    {
        switch (entity)
        {
            case CadText text:
                text.Height = height;
                break;
            case CadMText mtext:
                mtext.Height = height;
                break;
        }
    }

    /// <summary>
    /// Binary-search scales down text height to avoid overlapping nearby entities.
    /// Called after a successful text replacement in ProcessEntityCollection.
    /// Collects bounds of all OTHER entities in the same collection, checks for
    /// overlaps with the target entity, and scales height down (≥ 50% of original)
    /// to find the largest non-overlapping size.
    /// </summary>
    public static void ScaleDownToAvoidCollisions(
        CadEntity targetEntity,
        double originalHeight,
        IEnumerable<CadEntity> allEntities)
    {
        if (originalHeight <= 0) return;

        double currentHeight;
        switch (targetEntity)
        {
            case CadText text:
                currentHeight = text.Height;
                break;
            case CadMText mtext:
                currentHeight = mtext.Height;
                break;
            default:
                return;
        }

        double minHeight = originalHeight * 0.50;
        if (currentHeight <= minHeight) return;

        // Proportional collision margin (matches online path: originalHeight * 0.65)
        double collisionMargin = originalHeight * 0.65;

        // Collect bounds of all OTHER entities in the collection
        var otherBounds = new List<(double minX, double minY, double maxX, double maxY)>();
        foreach (var other in allEntities)
        {
            if (ReferenceEquals(other, targetEntity) || other == null) continue;
            var b = GetEntityBounds(other);
            if (b.HasValue) otherBounds.Add(b.Value);
        }

        if (otherBounds.Count == 0) return;

        // Check current bounds for any overlap
        var currentBounds = GetEntityBounds(targetEntity);
        if (!currentBounds.HasValue) return;

        bool hasCollision = false;
        foreach (var ob in otherBounds)
        {
            if (HasBoundsOverlap(currentBounds.Value, ob, collisionMargin))
            {
                hasCollision = true;
                break;
            }
        }

        if (!hasCollision) return;

        // Binary search for maximum non-overlapping height
        double lo = minHeight;
        double hi = currentHeight;
        double savedHeight = currentHeight;
        bool foundSafe = false;

        for (int iter = 0; iter < 15; iter++)
        {
            if (hi - lo < 0.005) break;

            double mid = (lo + hi) / 2;
            TrySetEntityHeight(targetEntity, mid);

            var testBounds = GetEntityBounds(targetEntity);
            if (!testBounds.HasValue) break;

            bool midHasCollision = false;
            foreach (var ob in otherBounds)
            {
                if (HasBoundsOverlap(testBounds.Value, ob, collisionMargin))
                {
                    midHasCollision = true;
                    break;
                }
            }

            if (midHasCollision)
                hi = mid;
            else
            {
                lo = mid;
                foundSafe = true;
            }
        }

        if (foundSafe)
            TrySetEntityHeight(targetEntity, lo);
        else
            TrySetEntityHeight(targetEntity, savedHeight);

        if (lo < savedHeight * 0.99)
        {
            Log.Debug("Collision-avoidance scaling: {Type} {Handle} height {Old:F2} -> {New:F2}",
                targetEntity.GetType().Name, targetEntity.Handle, savedHeight, lo);
        }
    }
}
