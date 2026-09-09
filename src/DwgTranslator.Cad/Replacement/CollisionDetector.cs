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
