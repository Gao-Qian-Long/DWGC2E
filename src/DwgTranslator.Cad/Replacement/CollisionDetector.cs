using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgTranslator.Cad.Replacement;

public static class CollisionDetector
{
    public const double MinCollisionAvoidanceScale = 0.35;

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
        double tolX = Math.Clamp(frameW * 0.005, 0.5, 3.0);
        double tolY = Math.Clamp(frameH * 0.005, 0.5, 3.0);
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

    public static Extents3d GetCorrectedBounds(Entity entity)
    {
        entity.RecordGraphicsModified(true);
        var rawBounds = entity.GeometricExtents;
        if (entity is MText mt && mt.Width > 0)
        {
            double aw = mt.ActualWidth;
            if (aw > 0 && aw < rawBounds.MaxPoint.X - rawBounds.MinPoint.X)
            {
                double cx = (rawBounds.MinPoint.X + rawBounds.MaxPoint.X) / 2.0;
                return new Extents3d(
                    new Point3d(cx - aw / 2.0, rawBounds.MinPoint.Y, 0),
                    new Point3d(cx + aw / 2.0, rawBounds.MaxPoint.Y, 0));
            }
        }
        return rawBounds;
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

            double padding = originalHeight * 0.40;
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
                var textBounds = textEntity.GeometricExtents;
                var otherBounds = other.GeometricExtents;
                return BoundsIntersect2D(textBounds, otherBounds);
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