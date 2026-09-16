using ACadSharp.Entities;
using CSMath;
using DwgTranslator.Core.Models;
using Serilog;

using CadEntity = ACadSharp.Entities.Entity;
using CadInsert = ACadSharp.Entities.Insert;
using CadLwPolyline = ACadSharp.Entities.LwPolyline;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Offline collision avoidance for ACadSharp writeback.
/// Strategy order (matches online path intent):
///   1. Keep original height
///   2. Prefer MText wrap / width constraint
///   3. Micro-nudge position (small translations)
///   4. Only then binary-search height down
///   5. Retest after every change
/// Uses AABB estimates (no true IntersectWith offline).
/// </summary>
internal static class DwgCollisionDetector
{
    public static (double minX, double minY, double maxX, double maxY)? GetEntityBounds(CadEntity entity, int depth = 0)
    {
        return entity switch
        {
            AttributeEntity att => DwgBoundsEstimator.EstimateTextBounds(att),
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
            // Point-like bounds for dims/mleaders so they don't over-claim collision area.
            Dimension dim => CadGeometryHelper.ComputeAabbFromPoints([
                    (dim.InsertionPoint.X, dim.InsertionPoint.Y) ]),
            MultiLeader mleader => mleader.ContextData?.TextLocation != null
                ? CadGeometryHelper.ComputeAabbFromPoints([
                    (mleader.ContextData.TextLocation.X, mleader.ContextData.TextLocation.Y) ])
                : null,
            _ => null
        };
    }

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

    public static bool HasBoundsOverlap(
        (double minX, double minY, double maxX, double maxY) a,
        (double minX, double minY, double maxX, double maxY) b,
        double margin = 0.0)
    {
        return a.minX - margin < b.maxX &&
               a.maxX + margin > b.minX &&
               a.minY - margin < b.maxY &&
               a.maxY + margin > b.minY;
    }

    /// <summary>
    /// Full offline collision resolution: wrap -> nudge -> scale, retest each step.
    /// </summary>
    public static void ResolveCollisions(
        CadEntity targetEntity,
        double originalHeight,
        IEnumerable<CadEntity> allEntities,
        CoreTextEntity? ourEntity = null)
    {
        if (originalHeight <= 0) return;
        if (targetEntity is not CadText and not CadMText) return;

        var others = BuildNearbyBounds(targetEntity, allEntities, originalHeight);
        if (others.Count == 0) return;

        double margin = originalHeight * WritebackConstants.CollisionMarginRatio;

        // Snapshot original geometry so we can restore on total failure paths.
        var snapshot = Snapshot(targetEntity);

        if (!HasCollision(targetEntity, others, margin))
            return;

        Log.Debug("Offline collision: {Type} {Handle} resolving (origH={H:F2})",
            targetEntity.GetType().Name, targetEntity.Handle, originalHeight);

        // Strategy 1: keep height, try wrap / width constraint (MText only)
        if (targetEntity is CadMText mtext)
        {
            if (TryWrapMText(mtext, originalHeight, ourEntity, others, margin))
            {
                Log.Debug("Offline collision: {Handle} resolved by wrap", mtext.Handle);
                return;
            }
        }

        // Strategy 2: micro-nudge position while keeping height
        if (TryNudge(targetEntity, originalHeight, others, margin))
        {
            Log.Debug("Offline collision: {Handle} resolved by nudge", targetEntity.Handle);
            return;
        }

        // Strategy 3: for DBText that is much wider, try converting layout via hard breaks on MText only;
        // DBText cannot wrap, so skip to scale.

        // Strategy 4: binary-search height reduction (last resort)
        if (TryScaleHeight(targetEntity, originalHeight, others, margin))
        {
            Log.Debug("Offline collision: {Handle} resolved by scale H={H:F2}",
                targetEntity.Handle, GetHeight(targetEntity));
            return;
        }

        // Residual overlap remains. Keep the least-bad state from scaling (already applied),
        // but never below hard min height.
        double hardMin = originalHeight * WritebackConstants.HardMinHeightRatio;
        if (GetHeight(targetEntity) < hardMin)
            SetHeight(targetEntity, hardMin);

        // If still worse than starting height-only change with original position, prefer
        // original position + min height rather than a large nudge that still collides.
        if (HasCollision(targetEntity, others, margin))
        {
            // Keep height as is (already scaled), restore position only.
            RestorePosition(targetEntity, snapshot);
            Log.Debug("Offline collision: {Handle} residual overlap after all strategies", targetEntity.Handle);
        }
    }

    /// <summary>
    /// Backward-compatible entry used by DwgTextReplacer. Delegates to full resolver.
    /// </summary>
    public static void ScaleDownToAvoidCollisions(
        CadEntity targetEntity,
        double originalHeight,
        IEnumerable<CadEntity> allEntities)
    {
        ResolveCollisions(targetEntity, originalHeight, allEntities, null);
    }

    private static List<(double minX, double minY, double maxX, double maxY)> BuildNearbyBounds(
        CadEntity targetEntity,
        IEnumerable<CadEntity> allEntities,
        double originalHeight)
    {
        var targetBounds = GetEntityBounds(targetEntity);
        if (!targetBounds.HasValue) return [];

        // Search radius: only consider entities near the text, not the whole drawing.
        double pad = originalHeight * WritebackConstants.CollisionMarginRatio * 4.0;
        var search = (
            targetBounds.Value.minX - pad,
            targetBounds.Value.minY - pad,
            targetBounds.Value.maxX + pad,
            targetBounds.Value.maxY + pad);

        var others = new List<(double, double, double, double)>();
        foreach (var other in allEntities)
        {
            if (other == null || ReferenceEquals(other, targetEntity)) continue;

            // Skip pure text entities that are far away; still include geometry.
            var b = GetEntityBounds(other);
            if (!b.HasValue) continue;
            if (!HasBoundsOverlap(search, b.Value, 0)) continue;

            // Ignore zero-area point bounds that would never truly collide meaningfully.
            double bw = b.Value.maxX - b.Value.minX;
            double bh = b.Value.maxY - b.Value.minY;
            if (bw < 1e-6 && bh < 1e-6) continue;

            others.Add(b.Value);
        }
        return others;
    }

    private static bool HasCollision(
        CadEntity target,
        List<(double minX, double minY, double maxX, double maxY)> others,
        double margin)
    {
        var tb = GetEntityBounds(target);
        if (!tb.HasValue) return false;
        foreach (var ob in others)
        {
            if (HasBoundsOverlap(tb.Value, ob, margin))
                return true;
        }
        return false;
    }

    private static bool TryWrapMText(
        CadMText mtext,
        double originalHeight,
        CoreTextEntity? ourEntity,
        List<(double minX, double minY, double maxX, double maxY)> others,
        double margin)
    {
        try
        {
            // Ensure height stays original during wrap attempts.
            mtext.Height = originalHeight;

            double originalRect = ourEntity?.MTextRectangleWidth > 0
                ? ourEntity.MTextRectangleWidth
                : (mtext.RectangleWidth > 0 ? mtext.RectangleWidth : 0);

            var bounds = GetEntityBounds(mtext);
            if (!bounds.HasValue) return false;
            double currentWidth = bounds.Value.maxX - bounds.Value.minX;
            if (currentWidth <= 0) return false;

            // Candidate wrap widths: prefer original rect, then progressive shrink.
            var candidates = new List<double>();
            if (originalRect > 0)
            {
                candidates.Add(originalRect);
                candidates.Add(originalRect * 0.95);
                candidates.Add(originalRect * 0.85);
            }
            candidates.Add(Math.Max(currentWidth * 0.85, originalHeight * 6));
            candidates.Add(Math.Max(currentWidth * 0.70, originalHeight * 5));
            candidates.Add(Math.Max(currentWidth * 0.55, originalHeight * 4));

            double savedWidth = mtext.RectangleWidth;
            foreach (var w in candidates.Distinct().OrderByDescending(x => x))
            {
                double width = Math.Max(w, WritebackConstants.MinMTextRectangleWidth);
                mtext.RectangleWidth = width;
                if (mtext.HasColumns && mtext.ColumnData != null)
                    mtext.ColumnData.ColumnType = ColumnType.NoColumns;

                if (!HasCollision(mtext, others, margin))
                    return true;
            }

            // Restore if wrap didn't help
            mtext.RectangleWidth = savedWidth;
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Offline TryWrapMText failed");
            return false;
        }
    }

    private static bool TryNudge(
        CadEntity target,
        double originalHeight,
        List<(double minX, double minY, double maxX, double maxY)> others,
        double margin)
    {
        var origin = GetPosition(target);
        if (!origin.HasValue) return false;

        // Keep height fixed at original for nudge phase.
        SetHeight(target, originalHeight);

        double step = originalHeight * 0.35;
        double maxNudge = originalHeight * WritebackConstants.MaxNudgeRatio;

        // Prefer small moves: right/left/up/down, then diagonals, then larger steps.
        var dirs = new (double dx, double dy)[]
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        };

        foreach (double scale in new[] { 1.0, 1.5, 2.0, 2.5 })
        {
            double dist = step * scale;
            if (dist > maxNudge) break;

            foreach (var (dx, dy) in dirs)
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                double nx = origin.Value.X + dx / len * dist;
                double ny = origin.Value.Y + dy / len * dist;
                SetPosition(target, nx, ny, origin.Value.Z);

                if (!HasCollision(target, others, margin))
                    return true;
            }
        }

        // Restore origin if no nudge worked
        SetPosition(target, origin.Value.X, origin.Value.Y, origin.Value.Z);
        return false;
    }

    private static bool TryScaleHeight(
        CadEntity target,
        double originalHeight,
        List<(double minX, double minY, double maxX, double maxY)> others,
        double margin)
    {
        double minHeight = originalHeight * WritebackConstants.MinHeightRatio;
        double hardMin = originalHeight * WritebackConstants.HardMinHeightRatio;
        double hi = GetHeight(target);
        if (hi <= 0) hi = originalHeight;
        if (hi <= minHeight)
        {
            // Already at floor - try hard min once more only if still colliding
            if (HasCollision(target, others, margin) && hi > hardMin)
            {
                SetHeight(target, hardMin);
                return !HasCollision(target, others, margin);
            }
            return !HasCollision(target, others, margin);
        }

        double lo = minHeight;
        double best = -1;
        double saved = hi;

        for (int i = 0; i < WritebackConstants.MaxBinarySearchIterations; i++)
        {
            if (hi - lo < 0.005) break;
            double mid = (lo + hi) / 2.0;
            SetHeight(target, mid);

            if (!HasCollision(target, others, margin))
            {
                best = mid;
                lo = mid; // try larger
            }
            else
            {
                hi = mid;
            }
        }

        if (best > 0)
        {
            SetHeight(target, best);
            return true;
        }

        // No fully clear height found. Apply hard min as best-effort, report failure.
        SetHeight(target, Math.Max(hardMin, Math.Min(saved, minHeight)));
        return !HasCollision(target, others, margin);
    }

    private static double GetHeight(CadEntity entity) => entity switch
    {
        CadText t => t.Height,
        CadMText m => m.Height,
        _ => 0
    };

    private static void SetHeight(CadEntity entity, double height)
    {
        switch (entity)
        {
            case CadText t: t.Height = height; break;
            case CadMText m: m.Height = height; break;
        }
    }

    private static (double X, double Y, double Z)? GetPosition(CadEntity entity) => entity switch
    {
        CadText t => (t.InsertPoint.X, t.InsertPoint.Y, t.InsertPoint.Z),
        CadMText m => (m.InsertPoint.X, m.InsertPoint.Y, m.InsertPoint.Z),
        _ => null
    };

    private static void SetPosition(CadEntity entity, double x, double y, double z)
    {
        switch (entity)
        {
            case CadText t:
                t.InsertPoint = new CSMath.XYZ(x, y, z);
                break;
            case CadMText m:
                m.InsertPoint = new CSMath.XYZ(x, y, z);
                break;
        }
    }

    private sealed class EntitySnapshot
    {
        public double Height;
        public double X, Y, Z;
        public double RectWidth;
        public bool HasRect;
    }

    private static EntitySnapshot Snapshot(CadEntity entity)
    {
        var s = new EntitySnapshot();
        switch (entity)
        {
            case CadText t:
                s.Height = t.Height;
                s.X = t.InsertPoint.X; s.Y = t.InsertPoint.Y; s.Z = t.InsertPoint.Z;
                break;
            case CadMText m:
                s.Height = m.Height;
                s.X = m.InsertPoint.X; s.Y = m.InsertPoint.Y; s.Z = m.InsertPoint.Z;
                s.RectWidth = m.RectangleWidth;
                s.HasRect = true;
                break;
        }
        return s;
    }

    private static void RestorePosition(CadEntity entity, EntitySnapshot s)
    {
        SetPosition(entity, s.X, s.Y, s.Z);
    }
}
