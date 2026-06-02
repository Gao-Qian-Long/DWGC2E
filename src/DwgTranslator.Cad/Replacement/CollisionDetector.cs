using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Provides geometric collision detection between text entities and other drawing entities.
/// </summary>
public static class CollisionDetector
{
    public const double MinCollisionAvoidanceScale = 0.5;
    private const int BinarySearchIterations = 12;

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

    /// <summary>
    /// Smart MText width adjustment to resolve interference without sacrificing text size.
    /// Tries 3 width ratios to change text layout via reflow.
    /// </summary>
    public static bool TryResolveMTextByWidthAdjustment(
        MText mtext,
        List<(Extents3d Bounds, double Padding)> colliders,
        double originalRectWidth)
    {
        try
        {
            mtext.RecordGraphicsModified(true);
            double origWidth = mtext.Width > 0 ? mtext.Width : originalRectWidth;
            if (origWidth <= 0) origWidth = 100;

            double[] widthRatios = { 0.55, 0.75, 1.40 };

            foreach (double ratio in widthRatios)
            {
                double testWidth = Math.Max(origWidth * ratio, 10.0);
                if (Math.Abs(testWidth - origWidth) < 1.0) continue;

                mtext.Width = testWidth;
                mtext.RecordGraphicsModified(true);

                try
                {
                    var testBounds = mtext.GeometricExtents;
                    bool collides = false;
                    for (int ci = 0; ci < colliders.Count; ci++)
                    {
                        double cPadding = colliders[ci].Padding;
                        if (BoundsIntersect2D(testBounds, colliders[ci].Bounds, cPadding))
                        { collides = true; break; }
                    }
                    if (!collides)
                    {
                        DwgTranslator.Cad.Log.Information("MText {Handle}: width={Orig:F1}→{New:F1} (ratio={R:F2}) resolved {Count} collisions",
                            mtext.Handle, origWidth, testWidth, ratio, colliders.Count);
                        return true;
                    }
                }
                catch { }
            }

            mtext.Width = origWidth;
            mtext.RecordGraphicsModified(true);
            return false;
        }
        catch (Exception ex)
        {
            DwgTranslator.Cad.Log.Debug("MText width adjustment failed for {Handle}: {Error}", mtext.Handle, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Binary-search for the largest MText width that avoids all collisions.
    /// </summary>
    public static bool TryShrinkMTextWidth(
        MText mText, Extents3d textBounds, List<Extents3d> colliders,
        double padding, double originalHeight)
    {
        double originalWidth = mText.Width;
        if (originalWidth <= 1.0) return false;

        double minWidth = Math.Max(originalWidth * 0.30, 1.0);
        double lowWidth = minWidth;
        double highWidth = originalWidth;
        double bestWidth = originalWidth;
        bool resolved = false;

        for (int i = 0; i < BinarySearchIterations; i++)
        {
            double midWidth = (lowWidth + highWidth) / 2.0;
            mText.Width = midWidth;
            mText.RecordGraphicsModified(true);

            try
            {
                var testBounds = mText.GeometricExtents;
                bool collides = false;
                foreach (var c in colliders)
                {
                    if (BoundsIntersect2D(testBounds, c, padding))
                    { collides = true; break; }
                }
                if (collides) lowWidth = midWidth;
                else { bestWidth = midWidth; highWidth = midWidth; resolved = true; }
            }
            catch { lowWidth = midWidth; }
        }

        if (resolved)
        {
            mText.Width = bestWidth;
            mText.RecordGraphicsModified(true);
        }
        else
        {
            mText.Width = originalWidth;
            mText.RecordGraphicsModified(true);
        }
        return resolved;
    }

    /// <summary>
    /// Returns the corrected bounding box for the given entity.
    /// For MText entities with non-zero Width, uses ActualWidth to avoid false-positive collisions.
    /// </summary>
    public static Extents3d GetCorrectedBounds(Entity entity)
    {
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

    /// <summary>
    /// Collects collider bounds WITH per-type paddings for accurate verification.
    /// </summary>
    public static List<(Extents3d Bounds, double Padding)> CollectEntityColliders(
        Entity textEntity, BlockTableRecord btr, Transaction tr,
        double trueOriginalHeight, Database? db = null)
    {
        var colliders = new List<Extents3d>();
        var positions = new List<Point3d>();
        var types = new List<string>();
        var paddings = new List<double>();

        try
        {
            textEntity.RecordGraphicsModified(true);
            Extents3d textBounds;
            try { textBounds = GetCorrectedBounds(textEntity); }
            catch { return new List<(Extents3d, double)>(); }

            double padding = trueOriginalHeight * 0.40;
            ColliderCollector.CollectPotentialColliders(tr, btr, textEntity.ObjectId, textBounds, padding,
                colliders, positions, 0, trackTypes: true, types, paddings);

            if (db != null)
                ColliderCollector.CollectCrossBlockColliders(tr, db, textEntity.ObjectId, textBounds, padding,
                    colliders, positions, trackTypes: true, types, paddings);
        }
        catch { }

        var result = new List<(Extents3d, double)>();
        for (int i = 0; i < colliders.Count; i++)
            result.Add((colliders[i], paddings[i]));
        return result;
    }
}
