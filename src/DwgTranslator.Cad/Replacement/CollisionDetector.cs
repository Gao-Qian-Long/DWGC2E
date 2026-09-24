#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.Geometry;
#endif
using WBC = DwgTranslator.Core.Models.WritebackConstants;

namespace DwgTranslator.Cad.Replacement;

public static class CollisionDetector
{
    public static readonly double MinCollisionAvoidanceScale = WBC.MinHeightRatio;

    public static Extents3d? FindClosestFrame(Point3d point, List<Extents3d> frames)
    {
        if (frames.Count == 0) return null;
        Extents3d? closest = null;
        double minDist = double.MaxValue;
        foreach (var frame in frames)
        {
            var center = new Point3d(
                (frame.MinPoint.X + frame.MaxPoint.X) / 2.0,
                (frame.MinPoint.Y + frame.MaxPoint.Y) / 2.0, 0);
            double dist = point.DistanceTo(center);
            if (dist < minDist) { minDist = dist; closest = frame; }
        }
        return closest;
    }

    public static bool ExceedsFrame(Extents3d bounds, Extents3d frame)
    {
        double frameW = frame.MaxPoint.X - frame.MinPoint.X;
        double frameH = frame.MaxPoint.Y - frame.MinPoint.Y;
        double tolX = DwgTranslator.Cad.Compat.Clamp(frameW * 0.005, 0.5, 3.0);
        double tolY = DwgTranslator.Cad.Compat.Clamp(frameH * 0.005, 0.5, 3.0);
        return bounds.MinPoint.X < frame.MinPoint.X - tolX
            || bounds.MaxPoint.X > frame.MaxPoint.X + tolX
            || bounds.MinPoint.Y < frame.MinPoint.Y - tolY
            || bounds.MaxPoint.Y > frame.MaxPoint.Y + tolY;
    }

    public static double ComputeOverflowRatio(Extents3d bounds, Extents3d frame)
    {
        double frameW = Math.Max(frame.MaxPoint.X - frame.MinPoint.X, 1.0);
        double frameH = Math.Max(frame.MaxPoint.Y - frame.MinPoint.Y, 1.0);
        double ol = Math.Max(0, frame.MinPoint.X - bounds.MinPoint.X);
        double or = Math.Max(0, bounds.MaxPoint.X - frame.MaxPoint.X);
        double ob = Math.Max(0, frame.MinPoint.Y - bounds.MinPoint.Y);
        double ot = Math.Max(0, bounds.MaxPoint.Y - frame.MaxPoint.Y);
        return Math.Max(Math.Max(ol, or) / frameW, Math.Max(ob, ot) / frameH);
    }

    public static bool BoundsIntersect2D(Extents3d a, Extents3d b, double padding = 0.0)
    {
        return a.MinPoint.X - padding < b.MaxPoint.X + padding
            && a.MaxPoint.X + padding > b.MinPoint.X - padding
            && a.MinPoint.Y - padding < b.MaxPoint.Y + padding
            && a.MaxPoint.Y + padding > b.MinPoint.Y - padding;
    }

    public static Extents3d GetCorrectedBounds(Entity entity, bool includeLayoutAdvance = true)
    {
        var previous=HostApplicationServices.WorkingDatabase;
        try
        {
            if(entity.Database!=null)HostApplicationServices.WorkingDatabase=entity.Database;
            return MeasureInk(entity, includeLayoutAdvance);
        }
        finally { HostApplicationServices.WorkingDatabase=previous; }
    }

    /// <summary>
    /// The four corners of a text entity's rendered rectangle, in world coordinates, or null when
    /// the shape cannot be reconstructed (callers then fall back to the axis-aligned box).
    ///
    /// The AutoCAD managed API only exposes axis-aligned <c>Extents3d</c>, which over-estimates
    /// rotated text badly: the bounding box of a 45-degree label is a diagonal square covering
    /// about twice the label's own area. Comparing those boxes reports overlaps between labels
    /// that never touch, which is what forced rotated labels through an unnecessary shrink (or a
    /// revert to the source text). Rotation-aware callers need the real rectangle.
    /// </summary>
    public static Point3d[]? TryGetOrientedCorners(Entity entity)
    {
        if (entity is not MText mtext) return null;
        try
        {
            double w = mtext.ActualWidth, h = mtext.ActualHeight;
            if (w <= 0 || h <= 0) return null;
            int attachment = (int)mtext.Attachment - 1;
            if (attachment < 0 || attachment > 8) return null;

            // Same attachment maths as MeasureInk's layout-advance rectangle, except the corners
            // are returned instead of being merged into an axis-aligned union.
            double x0 = -(attachment % 3) * w / 2, y0 = (attachment / 3) * h / 2;
            var u = mtext.Direction.GetNormal();
            var v = mtext.Normal.CrossProduct(u).GetNormal();
            // Perimeter order is mandatory for the separating-axis test below. The old nested
            // loops returned TL, BL, TR, BR and therefore treated a diagonal as an edge.
            return
            [
                mtext.Location + u * x0       + v * y0,
                mtext.Location + u * (x0 + w) + v * y0,
                mtext.Location + u * (x0 + w) + v * (y0 - h),
                mtext.Location + u * x0       + v * (y0 - h)
            ];
        }
        catch { return null; }
    }

    /// <summary>
    /// Separating-axis test for two convex quads. Returns false when the quads are apart, or when
    /// they overlap by less than <paramref name="tolerance"/> (a hairline touch is not a clash).
    /// </summary>
    public static bool QuadsOverlap(Point3d[] a, Point3d[] b, double tolerance = 0.01)
        => a.Length >= 3 && b.Length >= 3
           && !HasSeparatingAxis(a, b, tolerance)
           && !HasSeparatingAxis(b, a, tolerance);

    /// <summary>
    /// True when a finite 2D segment intersects a polygon or comes within the requested clearance.
    /// Used for rendered frame/cell boundaries after nested block transforms. Unlike an AABB test,
    /// this does not turn a diagonal line into a filled rectangle.
    /// </summary>
    public static bool SegmentWithinClearance(Point3d a, Point3d b, Point3d[] polygon, double clearance)
    {
        if (polygon == null || polygon.Length < 3) return false;
        if (PointInPolygon2D(a, polygon) || PointInPolygon2D(b, polygon)) return true;

        double limit = Math.Max(0, clearance);
        for (int i = 0; i < polygon.Length; i++)
        {
            var c = polygon[i];
            var d = polygon[(i + 1) % polygon.Length];
            if (SegmentsIntersect2D(a, b, c, d)) return true;
            if (SegmentDistance2D(a, b, c, d) <= limit) return true;
        }
        return false;
    }

    private static bool PointInPolygon2D(Point3d p, Point3d[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            bool crosses = (a.Y > p.Y) != (b.Y > p.Y);
            if (!crosses) continue;
            double denominator = b.Y - a.Y;
            if (Math.Abs(denominator) < 1e-12) continue;
            double x = (b.X - a.X) * (p.Y - a.Y) / denominator + a.X;
            if (p.X < x) inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect2D(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        const double eps = 1e-10;
        double abC = Cross2D(a, b, c), abD = Cross2D(a, b, d);
        double cdA = Cross2D(c, d, a), cdB = Cross2D(c, d, b);
        if (((abC > eps && abD < -eps) || (abC < -eps && abD > eps)) &&
            ((cdA > eps && cdB < -eps) || (cdA < -eps && cdB > eps))) return true;
        return Math.Abs(abC) <= eps && PointOnSegment2D(c, a, b, eps)
            || Math.Abs(abD) <= eps && PointOnSegment2D(d, a, b, eps)
            || Math.Abs(cdA) <= eps && PointOnSegment2D(a, c, d, eps)
            || Math.Abs(cdB) <= eps && PointOnSegment2D(b, c, d, eps);
    }

    private static double Cross2D(Point3d a, Point3d b, Point3d p) =>
        (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static bool PointOnSegment2D(Point3d p, Point3d a, Point3d b, double eps) =>
        p.X >= Math.Min(a.X, b.X) - eps && p.X <= Math.Max(a.X, b.X) + eps &&
        p.Y >= Math.Min(a.Y, b.Y) - eps && p.Y <= Math.Max(a.Y, b.Y) + eps;

    private static double SegmentDistance2D(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        if (SegmentsIntersect2D(a, b, c, d)) return 0;
        return Math.Min(
            Math.Min(PointSegmentDistance2D(a, c, d), PointSegmentDistance2D(b, c, d)),
            Math.Min(PointSegmentDistance2D(c, a, b), PointSegmentDistance2D(d, a, b)));
    }

    private static double PointSegmentDistance2D(Point3d p, Point3d a, Point3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length2 = dx * dx + dy * dy;
        if (length2 <= 1e-20)
        {
            double px = p.X - a.X, py = p.Y - a.Y;
            return Math.Sqrt(px * px + py * py);
        }
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double qx = a.X + t * dx, qy = a.Y + t * dy;
        double ex = p.X - qx, ey = p.Y - qy;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static bool HasSeparatingAxis(Point3d[] from, Point3d[] other, double tolerance)
    {
        for (int i = 0; i < from.Length; i++)
        {
            var p = from[i];
            var q = from[(i + 1) % from.Length];
            double nx = -(q.Y - p.Y), ny = q.X - p.X;
            if (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12) continue;

            double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
            foreach (var pt in from) { double d = pt.X * nx + pt.Y * ny; if (d < minA) minA = d; if (d > maxA) maxA = d; }
            foreach (var pt in other) { double d = pt.X * nx + pt.Y * ny; if (d < minB) minB = d; if (d > maxB) maxB = d; }

            if (maxA <= minB + tolerance || maxB <= minA + tolerance) return true;
        }
        return false;
    }

    private static Extents3d MeasureInk(Entity entity, bool includeLayoutAdvance)
    {
        entity.RecordGraphicsModified(true);
        if (entity is MText mtext)
        {
            // GstarCAD's MText extents may describe the wrap rectangle, not ink:
            // an unbreakable English word can extend far beyond that rectangle.
            var pieces = new DBObjectCollection();
            Extents3d? ink = null;
            try
            {
                mtext.Explode(pieces);
                foreach (DBObject piece in pieces)
                {
                    if (piece is not Entity glyph) continue;
                    var bounds = glyph.GeometricExtents;
                    if (ink.HasValue) { var union = ink.Value; union.AddExtents(bounds); ink = union; }
                    else ink = bounds;
                }
                // The host's text layout metrics may reserve more advance than
                // exploded glyph extents (notably Latin text printed as PDF).
                // Include that correctly rotated, attachment-aware rectangle too.
                if (includeLayoutAdvance && (int)mtext.Attachment >= 1 && (int)mtext.Attachment <= 9)
                {
                double w=mtext.ActualWidth, h=mtext.ActualHeight;
                int attachment=(int)mtext.Attachment-1;
                double x0=-(attachment%3)*w/2, y0=(attachment/3)*h/2;
                var u=mtext.Direction.GetNormal();
                var v=mtext.Normal.CrossProduct(u).GetNormal();
                foreach(var x in new[]{x0,x0+w})
                foreach(var y in new[]{y0,y0-h})
                {
                    var point=mtext.Location+u*x+v*y;
                    if(ink.HasValue){var union=ink.Value;union.AddPoint(point);ink=union;}
                    else ink=new Extents3d(point,point);
                }
                }
            }
            finally { foreach (DBObject piece in pieces) piece.Dispose(); }
            if (ink.HasValue) return ink.Value;
            throw new InvalidOperationException("MText has no measurable rendered glyphs");
        }
        return entity.GeometricExtents;
    }

    public static List<Entity> CollectNearbyEntities(
        Entity textEntity, BlockTableRecord btr, Transaction tr,
        double originalHeight)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);

            Extents3d textBounds;
            try { textBounds = GetCorrectedBounds(textEntity); }
            catch { return new List<Entity>(); }

            double padding = originalHeight * WBC.CollisionMarginRatio;
            return ColliderCollector.CollectPotentialColliders(tr, btr, textEntity.ObjectId, textBounds, padding);
        }
        catch { return new List<Entity>(); }
    }

    public static bool HasCollisionWithEntity(Entity textEntity, Entity other)
    {
        if (textEntity.ObjectId == other.ObjectId) return false;

        try
        {
            var pts = new Point3dCollection();
            textEntity.IntersectWith(other, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
            if (pts.Count > 0) return true;
        }
        catch
        {
            try
            {
                // Fallback only: use corrected bounds and tiny epsilon, not large padding,
                // to avoid false collisions with nearby non-touching geometry.
                var textBounds = GetCorrectedBounds(textEntity);
                var otherBounds = other.GeometricExtents;
                return BoundsIntersect2D(textBounds, otherBounds, 0.01);
            }
            catch { return false; }
        }

        return false;
    }

    public static bool HasAnyCollision(Entity textEntity, List<Entity> nearbyEntities)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);
        }
        catch { }

        for (int i = 0; i < nearbyEntities.Count; i++)
        {
            if (HasCollisionWithEntity(textEntity, nearbyEntities[i]))
                return true;
        }
        return false;
    }
}
