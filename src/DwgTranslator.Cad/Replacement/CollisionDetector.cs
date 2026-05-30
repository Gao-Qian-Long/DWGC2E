using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Provides geometric collision detection between text entities and other drawing entities.
/// Strategies (in order of preference):
/// 1. Text displacement: move text away from colliding geometry (8-direction search)
/// 2. MText width shrinking: for MText entities, try narrowing the bounding width
/// 3. Height scaling: reduce text height via binary search
/// </summary>
public static class CollisionDetector
{
    /// <summary>
    /// Minimum height ratio when scaling down to avoid collisions.
    /// </summary>
    public const double MinCollisionAvoidanceScale = 0.5;

    /// <summary>
    /// Number of binary search iterations for collision resolution.
    /// </summary>
    private const int BinarySearchIterations = 8;

    /// <summary>
    /// Maximum displacement distance as a multiple of text height.
    /// </summary>
    private const double MaxDisplacementRatio = 3.0;

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
    /// in the same block. Strategy:
    /// 1. Try displacing the text (8-direction search)
    /// 2. For MText: try narrowing the bounding width
    /// 3. Scale down text height via binary search
    /// Returns true if resolved.
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
            // Force graphics update so GeometricExtents is accurate
            textEntity.RecordGraphicsModified(true);

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

            double padding = originalHeight * 0.25; // safety margin (increased from 0.15)
            double minHeight = originalHeight * minHeightRatio;

            // Collect other entities that might collide (recursive into BlockReferences)
            var colliders = new List<Extents3d>();
            var colliderPositions = new List<Point3d>(); // center of each collider
            CollectPotentialColliders(tr, btr, textEntity.ObjectId, textBounds, padding, colliders, colliderPositions, 0);

            if (colliders.Count == 0)
                return true; // no collision

            // Strategy 1: Try displacing the text with 8-direction search
            if (TryDisplaceText(textEntity, textBounds, colliders, colliderPositions, originalHeight, padding))
            {
                Log.Information("Entity {Handle} displaced to avoid collision with {Count} entities",
                    textEntity.Handle, colliders.Count);
                return true;
            }

            // Strategy 2: For MText entities, try shrinking the width before scaling height
            if (textEntity is MText mText && mText.Width > 0)
            {
                if (TryShrinkMTextWidth(mText, textBounds, colliders, padding, originalHeight))
                {
                    Log.Information("Entity {Handle} width shrunk to avoid collision with {Count} entities",
                        textEntity.Handle, colliders.Count);
                    return true;
                }
            }

            // Strategy 3: Binary-search the largest height that avoids all collisions
            double lowHeight = minHeight;
            double highHeight = originalHeight;
            double bestHeight = originalHeight;
            bool resolved = false;

            for (int i = 0; i < BinarySearchIterations; i++)
            {
                double midHeight = (lowHeight + highHeight) / 2.0;
                SetTextHeight(textEntity, midHeight);
                textEntity.RecordGraphicsModified(true);

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
            textEntity.RecordGraphicsModified(true);

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

    /// <summary>
    /// Recursively collects potential collider bounds from a block table record,
    /// including entities inside nested BlockReferences.
    /// </summary>
    private static void CollectPotentialColliders(
        Transaction tr,
        BlockTableRecord btr,
        ObjectId skipId,
        Extents3d textBounds,
        double padding,
        List<Extents3d> colliders,
        List<Point3d> colliderPositions,
        int depth)
    {
        const int maxRecursionDepth = 5;
        if (depth > maxRecursionDepth) return;

        foreach (ObjectId otherId in btr)
        {
            if (otherId == skipId || !otherId.IsValid) continue;

            Entity? other;
            try
            {
                other = tr.GetObject(otherId, OpenMode.ForRead, false) as Entity;
            }
            catch { continue; }

            if (other == null) continue;

            // Recurse into BlockReferences to detect nested entity collisions
            if (other is BlockReference nestedBlockRef)
            {
                try
                {
                    var nestedBtr = (BlockTableRecord)tr.GetObject(
                        nestedBlockRef.BlockTableRecord, OpenMode.ForRead);
                    CollectPotentialColliders(tr, nestedBtr, skipId, textBounds, padding,
                        colliders, colliderPositions, depth + 1);
                }
                catch { /* skip unreachable blocks */ }
                continue;
            }

            // Skip Dimension entities — dimension text intentionally overlaps its own line
            if (other is Dimension)
                continue;

            // For text-to-text collisions, use smaller padding (they often coexist near each other)
            double collisionPadding = (other is DBText or MText or AttributeReference)
                ? padding * 0.3
                : padding;

            try
            {
                var otherBounds = other.GeometricExtents;
                if (BoundsIntersect2D(textBounds, otherBounds, collisionPadding))
                {
                    colliders.Add(otherBounds);
                    colliderPositions.Add(new Point3d(
                        (otherBounds.MinPoint.X + otherBounds.MaxPoint.X) / 2.0,
                        (otherBounds.MinPoint.Y + otherBounds.MaxPoint.Y) / 2.0,
                        0));
                }
            }
            catch { /* ignore entities without valid extents */ }
        }
    }

    /// <summary>
    /// Attempts to shrink MText width to avoid collisions before scaling height.
    /// Uses binary search on the width between a minimum (30% of original) and current width.
    /// Returns true if width shrinking resolved the collision.
    /// </summary>
    private static bool TryShrinkMTextWidth(
        MText mText,
        Extents3d textBounds,
        List<Extents3d> colliders,
        double padding,
        double originalHeight)
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
                    lowWidth = midWidth;
                }
                else
                {
                    bestWidth = midWidth;
                    highWidth = midWidth;
                    resolved = true;
                }
            }
            catch
            {
                lowWidth = midWidth;
            }
        }

        if (resolved)
        {
            mText.Width = bestWidth;
            mText.RecordGraphicsModified(true);
        }
        else
        {
            // Restore original width
            mText.Width = originalWidth;
            mText.RecordGraphicsModified(true);
        }

        return resolved;
    }

    /// <summary>
    /// Attempts to displace (move) the text entity away from colliding geometry.
    /// Tries 8 directions and picks the one with the shortest displacement
    /// that avoids all collisions.
    /// Returns true if displacement succeeded.
    /// </summary>
    private static bool TryDisplaceText(
        Entity textEntity,
        Extents3d textBounds,
        List<Extents3d> colliders,
        List<Point3d> colliderPositions,
        double originalHeight,
        double padding)
    {
        // Get the text insertion point / location
        Point3d textLocation = GetTextLocation(textEntity);
        double textCenterX = (textBounds.MinPoint.X + textBounds.MaxPoint.X) / 2.0;
        double textCenterY = (textBounds.MinPoint.Y + textBounds.MaxPoint.Y) / 2.0;

        // Try displacements in 8 directions (cardinal + diagonal)
        double diag = Math.Sqrt(2) / 2.0; // ~0.707
        var directions = new (double dx, double dy, string name)[]
        {
            (1, 0, "right"),
            (-1, 0, "left"),
            (0, 1, "up"),
            (0, -1, "down"),
            (diag, diag, "up-right"),
            (-diag, diag, "up-left"),
            (diag, -diag, "down-right"),
            (-diag, -diag, "down-left"),
        };

        double bestDistance = double.MaxValue;
        Vector3d bestDisplacement = new Vector3d(0, 0, 0);
        bool found = false;

        foreach (var (dx, dy, name) in directions)
        {
            // Calculate displacement needed: estimate how far to move
            // based on the overlap amount with the nearest collider
            double stepSize = originalHeight * 1.0; // increased from 0.5

            for (int step = 1; step <= 6; step++) // up to 6 heights distance
            {
                double displacement = stepSize * step;
                double newX = textCenterX + dx * displacement;
                double newY = textCenterY + dy * displacement;

                // Estimate displaced bounds
                double textW = textBounds.MaxPoint.X - textBounds.MinPoint.X;
                double textH = textBounds.MaxPoint.Y - textBounds.MinPoint.Y;
                var displacedBounds = new Extents3d(
                    new Point3d(newX - textW / 2, newY - textH / 2, 0),
                    new Point3d(newX + textW / 2, newY + textH / 2, 0));

                // Check if displaced bounds avoid all colliders
                bool avoidsAll = true;
                foreach (var c in colliders)
                {
                    if (BoundsIntersect2D(displacedBounds, c, padding))
                    {
                        avoidsAll = false;
                        break;
                    }
                }

                if (avoidsAll && displacement < bestDistance)
                {
                    bestDistance = displacement;
                    bestDisplacement = new Vector3d(dx * displacement, dy * displacement, 0);
                    found = true;
                    break; // Found a working displacement in this direction
                }
            }
        }

        if (!found) return false;

        // Apply the displacement
        DisplaceText(textEntity, bestDisplacement);
        textEntity.RecordGraphicsModified(true);

        // Verify the displacement actually resolved the collision
        try
        {
            var newBounds = textEntity.GeometricExtents;
            foreach (var c in colliders)
            {
                if (BoundsIntersect2D(newBounds, c, padding))
                {
                    // Displacement didn't fully resolve — revert
                    DisplaceText(textEntity, -bestDisplacement);
                    textEntity.RecordGraphicsModified(true);
                    return false;
                }
            }
        }
        catch
        {
            // Can't verify — assume success
        }

        return true;
    }

    /// <summary>
    /// Gets the insertion point / location of a text entity.
    /// </summary>
    private static Point3d GetTextLocation(Entity entity)
    {
        return entity switch
        {
            AttributeReference att => att.Position,
            DBText dbText => dbText.Position,
            MText mText => mText.Location,
            _ => Point3d.Origin
        };
    }

    /// <summary>
    /// Displaces a text entity by the given vector.
    /// </summary>
    private static void DisplaceText(Entity entity, Vector3d displacement)
    {
        switch (entity)
        {
            case AttributeReference att:
                att.Position = att.Position + displacement;
                break;
            case DBText dbText:
                dbText.Position = dbText.Position + displacement;
                break;
            case MText mText:
                mText.Location = mText.Location + displacement;
                break;
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