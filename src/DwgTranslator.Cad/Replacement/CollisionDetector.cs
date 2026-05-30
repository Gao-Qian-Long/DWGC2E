using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Serilog;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Provides geometric collision detection between text entities and drawing frames or other entities.
/// </summary>
public static class CollisionDetector
{
    /// <summary>
    /// Minimum height ratio when scaling down to avoid collisions.
    /// </summary>
    public const double MinCollisionAvoidanceScale = 0.5;

    /// <summary>
    /// Finds the closest frame to a given point (usually text insertion point).
    /// Returns null if no frames are provided.
    /// </summary>
    public static Extents3d? FindClosestFrame(Point3d point, List<Extents3d> frames)
    {
        if (frames.Count == 0) return null;

        Extents3d? closest = null;
        double minDist = double.MaxValue;

        foreach (var frame in frames)
        {
            var center = new Point3d(
                (frame.MinPoint.X + frame.MaxPoint.X) / 2.0,
                (frame.MinPoint.Y + frame.MaxPoint.Y) / 2.0,
                0);
            double dist = point.DistanceTo(center);
            if (dist < minDist)
            {
                minDist = dist;
                closest = frame;
            }
        }
        return closest;
    }

    /// <summary>
    /// Checks if the given bounds exceed (overflow) the frame boundary.
    /// Allows a small tolerance (1% inside frame) to account for floating-point errors.
    /// </summary>
    public static bool ExceedsFrame(Extents3d bounds, Extents3d frame)
    {
        double tolX = (frame.MaxPoint.X - frame.MinPoint.X) * 0.01;
        double tolY = (frame.MaxPoint.Y - frame.MinPoint.Y) * 0.01;

        return bounds.MinPoint.X < frame.MinPoint.X - tolX
            || bounds.MaxPoint.X > frame.MaxPoint.X + tolX
            || bounds.MinPoint.Y < frame.MinPoint.Y - tolY
            || bounds.MaxPoint.Y > frame.MaxPoint.Y + tolY;
    }

    /// <summary>
    /// Computes the overflow ratio: how much the bounds exceed the frame.
    /// Returns 0 if fully inside, >0 if overflowing.
    /// </summary>
    public static double ComputeOverflowRatio(Extents3d bounds, Extents3d frame)
    {
        double frameW = Math.Max(frame.MaxPoint.X - frame.MinPoint.X, 1.0);
        double frameH = Math.Max(frame.MaxPoint.Y - frame.MinPoint.Y, 1.0);

        double overflowLeft = Math.Max(0, frame.MinPoint.X - bounds.MinPoint.X);
        double overflowRight = Math.Max(0, bounds.MaxPoint.X - frame.MaxPoint.X);
        double overflowBottom = Math.Max(0, frame.MinPoint.Y - bounds.MinPoint.Y);
        double overflowTop = Math.Max(0, bounds.MaxPoint.Y - frame.MaxPoint.Y);

        double maxOverflowRatio = Math.Max(
            Math.Max(overflowLeft, overflowRight) / frameW,
            Math.Max(overflowBottom, overflowTop) / frameH);

        return maxOverflowRatio;
    }

    /// <summary>
    /// Checks whether two bounding boxes intersect in 2D (ignoring Z).
    /// </summary>
    public static bool BoundsIntersect2D(Extents3d a, Extents3d b, double padding = 0.0)
    {
        return a.MinPoint.X - padding < b.MaxPoint.X + padding
            && a.MaxPoint.X + padding > b.MinPoint.X - padding
            && a.MinPoint.Y - padding < b.MaxPoint.Y + padding
            && a.MaxPoint.Y + padding > b.MinPoint.Y - padding;
    }

    /// <summary>
    /// Attempts to resolve collisions between the given text entity and other entities
    /// in the same block by scaling down the text height. Returns true if resolved.
    /// </summary>
    public static bool TryResolveCollisionByScaling(
        Entity textEntity,
        BlockTableRecord btr,
        Transaction tr,
        double originalHeight,
        double minHeightRatio = MinCollisionAvoidanceScale)
    {
        try
        {
            // Get text bounds after translation
            Extents3d textBounds;
            try
            {
                textBounds = textEntity.GeometricExtents;
            }
            catch
            {
                // Degenerate text can't collide
                return true;
            }

            double padding = originalHeight * 0.15; // small safety margin
            double minHeight = originalHeight * minHeightRatio;

            // Collect other entities that might collide (exclude text-like entities)
            var colliders = new List<Extents3d>();
            foreach (ObjectId otherId in btr)
            {
                if (otherId == textEntity.ObjectId) continue;
                if (!otherId.IsValid) continue;

                Entity? other;
                try
                {
                    other = tr.GetObject(otherId, OpenMode.ForRead, false) as Entity;
                }
                catch { continue; }

                if (other == null) continue;

                // Skip other text entities (labels often intentionally overlap near leaders)
                if (other is DBText or MText or AttributeReference or Dimension)
                    continue;

                try
                {
                    var otherBounds = other.GeometricExtents;
                    if (BoundsIntersect2D(textBounds, otherBounds, padding))
                    {
                        colliders.Add(otherBounds);
                    }
                }
                catch { /* ignore entities without valid extents */ }
            }

            if (colliders.Count == 0)
                return true; // no collision

            // Binary-search the largest height that avoids all collisions
            double lowHeight = minHeight;
            double highHeight = originalHeight;
            double bestHeight = originalHeight;
            bool resolved = false;

            for (int i = 0; i < 6; i++)
            {
                double midHeight = (lowHeight + highHeight) / 2.0;
                SetTextHeight(textEntity, midHeight);

                try
                {
                    var testBounds = textEntity.GeometricExtents;
                    bool stillCollides = false;
                    foreach (var c in colliders)
                    {
                        if (BoundsIntersect2D(testBounds, c, padding))
                        {
                            stillCollides = true;
                            break;
                        }
                    }

                    if (stillCollides)
                    {
                        highHeight = midHeight;
                    }
                    else
                    {
                        bestHeight = midHeight;
                        lowHeight = midHeight;
                        resolved = true;
                    }
                }
                catch
                {
                    highHeight = midHeight;
                }
            }

            SetTextHeight(textEntity, bestHeight);

            if (!resolved)
            {
                Log.Warning("Entity {Handle} collides with {Count} geometry entities even at min height {H:F2}",
                    textEntity.Handle, colliders.Count, minHeight);
            }
            else if (bestHeight < originalHeight)
            {
                Log.Information("Entity {Handle} scaled from {Orig:F2} to {New:F2} to avoid collision with {Count} entities",
                    textEntity.Handle, originalHeight, bestHeight, colliders.Count);
            }

            return resolved;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Collision resolution failed for entity {Handle}", textEntity.Handle);
            return false;
        }
    }

    private static void SetTextHeight(Entity entity, double height)
    {
        switch (entity)
        {
            case AttributeReference att:
                att.Height = height;
                break;
            case DBText dbText:
                dbText.Height = height;
                break;
            case MText mText:
                mText.TextHeight = height;
                break;
        }
    }
}
