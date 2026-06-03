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
        return entity switch
        {
            CadText text => DwgBoundsEstimator.EstimateTextBounds(text),
            CadMText mtext => DwgBoundsEstimator.EstimateMTextBounds(mtext),
            CadInsert insert => CadGeometryHelper.EstimateInsertBounds(insert, GetEntityBounds, depth + 1),
            CadLwPolyline poly => poly.Vertices.Count > 0
                ? CadGeometryHelper.ComputeAabbFromPoints(poly.Vertices.Select(v => (v.Location.X, v.Location.Y)))
                : null,
            Line line => (
                Math.Min(line.StartPoint.X, line.EndPoint.X),
                Math.Min(line.StartPoint.Y, line.EndPoint.Y),
                Math.Max(line.StartPoint.X, line.EndPoint.X),
                Math.Max(line.StartPoint.Y, line.EndPoint.Y)),
            Arc arc => EstimateArcBounds(arc),
            Circle circle => (
                circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius,
                circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius),
            Spline spline => spline.ControlPoints.Count > 0
                ? CadGeometryHelper.ComputeAabbFromPoints(spline.ControlPoints.Select(p => (p.X, p.Y)))
                : null,
            _ => null
        };
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
                lo = mid;
        }

        // Apply best-effort height (lo is the largest non-overlapping height found,
        // or minHeight if every height in the range still had collisions)
        TrySetEntityHeight(targetEntity, lo);

        if (lo < savedHeight * 0.99)
        {
            Log.Debug("Collision-avoidance scaling: {Type} {Handle} height {Old:F2} -> {New:F2}",
                targetEntity.GetType().Name, targetEntity.Handle, savedHeight, lo);
        }
    }
}
