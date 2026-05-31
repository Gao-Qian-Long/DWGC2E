using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Provides geometric collision detection between text entities and other drawing entities.
/// Strategies (in order of preference):
/// 1. Smart displacement: push text away from the center-of-mass of colliding geometry
/// 2. MText width shrinking: binary search for a width that avoids collision
/// 3. Height scaling: binary search for a height that avoids collision
/// </summary>
public static class CollisionDetector
{
    public const double MinCollisionAvoidanceScale = 0.5;
    private const int BinarySearchIterations = 8;
    private const int MaxDisplacementSteps = 10;
    private const double DisplacementStepSize = 1.2; // multiple of text height

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
        double tolX = (frame.MaxPoint.X - frame.MinPoint.X) * 0.01;
        double tolY = (frame.MaxPoint.Y - frame.MinPoint.Y) * 0.01;
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
    /// Attempts to resolve collisions between the given text entity and other entities
    /// in the same block. Strategy:
    /// 1. Smart displacement: push away from collision center-of-mass
    /// 2. For MText: width shrinking (including free-width fallback)
    /// 3. Height scaling via binary search
    /// Returns true if resolved.
    /// </summary>
    public static bool TryResolveCollisionByScaling(
        Entity textEntity,
        BlockTableRecord btr,
        Transaction tr,
        double originalHeight,
        double minHeightRatio = MinCollisionAvoidanceScale,
        Extents3d? frame = null)
    {
        return TryResolveCollisionByScaling(textEntity, btr, tr, originalHeight, minHeightRatio, frame, db: null);
    }

    /// <summary>
    /// Overload that optionally scans ALL block table records for cross-block collision detection.
    /// When <paramref name="db"/> is provided and no collision is found in the entity's own block,
    /// it will also check entities in other blocks (e.g. other paper space layouts).
    /// </summary>
    public static bool TryResolveCollisionByScaling(
        Entity textEntity,
        BlockTableRecord btr,
        Transaction tr,
        double originalHeight,
        double minHeightRatio = MinCollisionAvoidanceScale,
        Extents3d? frame = null,
        Database? db = null)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);

            Extents3d textBounds;
            try { textBounds = textEntity.GeometricExtents; }
            catch { return true; } // degenerate text

            double padding = originalHeight * 0.40; // safety margin
            double minHeight = originalHeight * minHeightRatio;

            var colliders = new List<Extents3d>();
            var colliderPositions = new List<Point3d>();
            CollectPotentialColliders(tr, btr, textEntity.ObjectId, textBounds, padding, colliders, colliderPositions, 0);

            // Cross-block scan: check other block table records for collisions
            if (colliders.Count == 0 && db != null)
            {
                CollectCrossBlockColliders(tr, db, textEntity.ObjectId, textBounds, padding, colliders, colliderPositions);
            }

            if (colliders.Count == 0)
                return true;

            // Strategy 1: Smart displacement — push AWAY from collider center-of-mass
            if (SmartDisplace(textEntity, textBounds, colliders, colliderPositions, originalHeight, padding, frame))
            {
                Log.Information("Entity {Handle} displaced to avoid collision with {Count} entities",
                    textEntity.Handle, colliders.Count);
                return true;
            }

            // Strategy 2: MText width shrinking
            if (textEntity is MText mText)
            {
                if (mText.Width > 0)
                {
                    if (TryShrinkMTextWidth(mText, textBounds, colliders, padding, originalHeight))
                    {
                        Log.Information("Entity {Handle} width shrunk to avoid collision",
                            textEntity.Handle);
                        return true;
                    }
                }
                else
                {
                    // Free-width: compute conservative width from content, then retry
                    if (TryConstrainFreeWidthAndShrink(mText, textBounds, colliders, padding, originalHeight))
                    {
                        Log.Information("Entity {Handle} free-width MText constrained to avoid collision",
                            textEntity.Handle);
                        return true;
                    }
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
                        { stillCollides = true; break; }
                    }
                    if (stillCollides) highHeight = midHeight;
                    else { bestHeight = midHeight; lowHeight = midHeight; resolved = true; }
                }
                catch { highHeight = midHeight; }
            }

            SetTextHeight(textEntity, bestHeight);
            textEntity.RecordGraphicsModified(true);

            if (!resolved)
                Log.Warning("Entity {Handle}: unresolved collision with {Count} entities at min height",
                    textEntity.Handle, colliders.Count);
            else if (bestHeight < originalHeight)
                Log.Information("Entity {Handle}: scaled {Orig:F2}→{New:F2} to avoid {Count} collisions",
                    textEntity.Handle, originalHeight, bestHeight, colliders.Count);

            return resolved;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Collision resolution failed for entity {Handle}", textEntity.Handle);
            return false;
        }
    }

    /// <summary>
    /// Recursively collects potential collider bounds from a block table record.
    /// Includes: lines, polylines, arcs, circles, splines, leaders, hatches, solids,
    /// text entities, and nested block references.
    /// </summary>
    private static void CollectPotentialColliders(
        Transaction tr, BlockTableRecord btr, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> colliderPositions, int depth)
    {
        const int maxDepth = 5;
        if (depth > maxDepth) return;

        foreach (ObjectId otherId in btr)
        {
            if (otherId == skipId || !otherId.IsValid) continue;

            Entity? other;
            try { other = tr.GetObject(otherId, OpenMode.ForRead, false) as Entity; }
            catch { continue; }
            if (other == null) continue;

            // Recurse into BlockReferences for nested geometry
            if (other is BlockReference nestedBr)
            {
                try
                {
                    var nestedBtr = (BlockTableRecord)tr.GetObject(nestedBr.BlockTableRecord, OpenMode.ForRead);
                    CollectPotentialColliders(tr, nestedBtr, skipId, textBounds, padding,
                        colliders, colliderPositions, depth + 1);
                }
                catch { }
                continue;
            }

            // Skip Dimension (intentional text/line overlap)
            if (other is Dimension) continue;
            // Skip Viewport (boundary only, not visual)
            if (other is Viewport) continue;

            // Classify entity type for collision padding
            bool isLineLike = other is Line or Polyline or Arc or Circle or Spline
                              or Polyline2d or Polyline3d or MLeader;
            bool isSolidLike = other is Solid or Solid3d or Region or Hatch;
            bool isTextLike = other is DBText or MText or AttributeReference;

            double collisionPadding;
            if (isTextLike)      collisionPadding = padding * 0.3;  // text can coexist closely
            else if (isLineLike) collisionPadding = padding * 1.3;  // extra for visually disruptive lines
            else if (isSolidLike) collisionPadding = padding * 0.8; // moderate for filled areas
            else                 collisionPadding = padding;        // default

            try
            {
                var otherBounds = other.GeometricExtents;
                if (BoundsIntersect2D(textBounds, otherBounds, collisionPadding))
                {
                    colliders.Add(otherBounds);
                    colliderPositions.Add(new Point3d(
                        (otherBounds.MinPoint.X + otherBounds.MaxPoint.X) / 2.0,
                        (otherBounds.MinPoint.Y + otherBounds.MaxPoint.Y) / 2.0, 0));
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Scans ALL block table records in the database for potential cross-block collisions.
    /// Skips the model space block (already scanned by the caller) and anonymous blocks.
    /// </summary>
    private static void CollectCrossBlockColliders(
        Transaction tr, Database db, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> colliderPositions)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
            catch { continue; }

            // Skip model space (already scanned by the caller) and anonymous blocks
            if (btr.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (btr.IsAnonymous) continue;
            // Skip paper space layout records (they reference the same underlying BTR)
            if (btr.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;

            CollectPotentialColliders(tr, btr, skipId, textBounds, padding,
                colliders, colliderPositions, 0);
        }
    }

    /// <summary>
    /// Adjusts a text entity's position so its bounding box stays within the given frame boundary.
    /// Displaces the text by the minimum amount needed to bring it fully inside the frame.
    /// </summary>
    public static void AdjustTextStartPosition(Entity textEntity, Extents3d frame)
    {
        try
        {
            var bounds = textEntity.GeometricExtents;
            var displacement = new Vector3d(0, 0, 0);

            if (bounds.MinPoint.X < frame.MinPoint.X)
                displacement += new Vector3d(frame.MinPoint.X - bounds.MinPoint.X, 0, 0);
            if (bounds.MaxPoint.X > frame.MaxPoint.X)
                displacement += new Vector3d(frame.MaxPoint.X - bounds.MaxPoint.X, 0, 0);
            if (bounds.MinPoint.Y < frame.MinPoint.Y)
                displacement += new Vector3d(0, frame.MinPoint.Y - bounds.MinPoint.Y, 0);
            if (bounds.MaxPoint.Y > frame.MaxPoint.Y)
                displacement += new Vector3d(0, frame.MaxPoint.Y - bounds.MaxPoint.Y, 0);

            if (displacement.Length > 0.01)
            {
                DisplaceText(textEntity, displacement);
                textEntity.RecordGraphicsModified(true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Binary-search for the largest MText width (between 30% and current width)
    /// that avoids all collisions. Returns true if resolved.
    /// </summary>
    private static bool TryShrinkMTextWidth(
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
    /// For free-width MText: parse content to find the widest line, set an initial
    /// conservative width (maxLineWidth with headroom), then binary-search shrink.
    /// The initial width uses 20% headroom (not 10%) to avoid the "too tight" side effect.
    /// </summary>
    private static bool TryConstrainFreeWidthAndShrink(
        MText mText, Extents3d textBounds, List<Extents3d> colliders,
        double padding, double originalHeight)
    {
        try
        {
            string contents = mText.Contents;
            if (string.IsNullOrEmpty(contents)) return false;

            var lines = contents.Split(new[] { "\\P" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            double maxLineWidth = 0;
            foreach (var line in lines)
            {
                double w = Core.Services.TextWidthEstimator.EstimateTextWidth(line, mText.TextHeight);
                if (w > maxLineWidth) maxLineWidth = w;
            }
            if (maxLineWidth <= 0) return false;

            // Use 20% headroom to avoid re-introducing word stretching
            double initialWidth = Math.Max(maxLineWidth * 1.20, 10.0);
            mText.Width = initialWidth;
            mText.RecordGraphicsModified(true);

            return TryShrinkMTextWidth(mText, textBounds, colliders, padding, originalHeight);
        }
        catch { return false; }
    }

    /// <summary>
    /// Smart displacement: computes the direction AWAY from the center-of-mass
    /// of all colliding entities, then searches for a displacement that avoids
    /// all collisions while staying within the frame (if provided).
    /// 
    /// The search explores a cone around the ideal "push-away" direction with
    /// increasing distance, falling back to full 360-degree scan if needed.
    /// </summary>
    private static bool SmartDisplace(
        Entity textEntity, Extents3d textBounds,
        List<Extents3d> colliders, List<Point3d> colliderPositions,
        double originalHeight, double padding, Extents3d? frame)
    {
        double textCenterX = (textBounds.MinPoint.X + textBounds.MaxPoint.X) / 2.0;
        double textCenterY = (textBounds.MinPoint.Y + textBounds.MaxPoint.Y) / 2.0;
        double textW = textBounds.MaxPoint.X - textBounds.MinPoint.X;
        double textH = textBounds.MaxPoint.Y - textBounds.MinPoint.Y;

        // Compute "center of pressure" — the average position of all colliders
        double colliderCx = 0, colliderCy = 0;
        foreach (var p in colliderPositions)
        {
            colliderCx += p.X;
            colliderCy += p.Y;
        }
        colliderCx /= colliderPositions.Count;
        colliderCy /= colliderPositions.Count;

        // Push AWAY from the collider center-of-mass
        double pushDx = textCenterX - colliderCx;
        double pushDy = textCenterY - colliderCy;
        double pushLen = Math.Sqrt(pushDx * pushDx + pushDy * pushDy);
        if (pushLen < 0.01)
        {
            pushDx = 1; pushDy = 0; pushLen = 1; // default: push right
        }
        pushDx /= pushLen;
        pushDy /= pushLen;

        // Generate search directions: start from the ideal push direction,
        // then fan out to ±45°, ±90°, ±135°, 180°
        double[] angles = { 0, 30, -30, 60, -60, 90, -90, 135, -135, 180 };
        double baseAngle = Math.Atan2(pushDy, pushDx) * 180.0 / Math.PI;

        double bestDistance = double.MaxValue;
        Vector3d bestDisplacement = new(0, 0, 0);
        bool found = false;

        foreach (double angleOffset in angles)
        {
            double rad = (baseAngle + angleOffset) * Math.PI / 180.0;
            double dx = Math.Cos(rad);
            double dy = Math.Sin(rad);

            double stepSize = originalHeight * DisplacementStepSize;

            for (int step = 1; step <= MaxDisplacementSteps; step++)
            {
                double dist = stepSize * step;
                double newX = textCenterX + dx * dist;
                double newY = textCenterY + dy * dist;

                var displacedBounds = new Extents3d(
                    new Point3d(newX - textW / 2, newY - textH / 2, 0),
                    new Point3d(newX + textW / 2, newY + textH / 2, 0));

                if (frame.HasValue && ExceedsFrame(displacedBounds, frame.Value))
                    continue;

                bool avoidsAll = true;
                foreach (var c in colliders)
                {
                    if (BoundsIntersect2D(displacedBounds, c, padding))
                    { avoidsAll = false; break; }
                }

                if (avoidsAll && dist < bestDistance)
                {
                    bestDistance = dist;
                    bestDisplacement = new Vector3d(dx * dist, dy * dist, 0);
                    found = true;
                    break;
                }
            }
        }

        if (!found) return false;

        // Reject unreasonably large displacements (more than 3x the text height)
        double maxReasonableDist = originalHeight * 3.0;
        if (bestDisplacement.Length > maxReasonableDist)
            return false;

        // Apply and verify
        DisplaceText(textEntity, bestDisplacement);
        textEntity.RecordGraphicsModified(true);

        try
        {
            var newBounds = textEntity.GeometricExtents;
            if (frame.HasValue && ExceedsFrame(newBounds, frame.Value))
            {
                DisplaceText(textEntity, -bestDisplacement);
                textEntity.RecordGraphicsModified(true);
                return false;
            }
            foreach (var c in colliders)
            {
                if (BoundsIntersect2D(newBounds, c, padding))
                {
                    DisplaceText(textEntity, -bestDisplacement);
                    textEntity.RecordGraphicsModified(true);
                    return false;
                }
            }
        }
        catch { }

        return true;
    }

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

    private static void DisplaceText(Entity entity, Vector3d displacement)
    {
        switch (entity)
        {
            case AttributeReference att: att.Position += displacement; break;
            case DBText dbText: dbText.Position += displacement; break;
            case MText mText: mText.Location += displacement; break;
        }
    }

    private static void SetTextHeight(Entity entity, double height)
    {
        switch (entity)
        {
            case AttributeReference att: att.Height = height; break;
            case DBText dbText: dbText.Height = height; break;
            case MText mText: mText.TextHeight = height; break;
        }
    }

    /// <summary>
    /// Resolves overlaps between a translated text entity and OTHER translated text entities.
    /// Only SCALES the text entity — never displaces it.
    ///
    /// Parameters:
    ///   currentHeight  — the entity's CURRENT height (upper bound for binary search)
    ///   trueOriginalHeight — the TRUE un-scaled original height (floor = ratio * this)
    /// </summary>
    /// <returns>true if the entity was scaled</returns>
    public static bool ResolveEntityOverlaps(
        Entity textEntity,
        List<Entity> otherEntities,
        double currentHeight,
        double trueOriginalHeight,
        double minHeightRatio = 0.65,
        double paddingRatio = 0.12)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);
            Extents3d myBounds;
            try { myBounds = textEntity.GeometricExtents; }
            catch { return false; }

            double padding = trueOriginalHeight * paddingRatio;
            double minHeight = trueOriginalHeight * minHeightRatio;
            double searchMax = Math.Max(currentHeight, minHeight);

            // Check for actual overlaps with any other translated entity
            var overlappingHandles = new List<string>();
            foreach (var other in otherEntities)
            {
                if (ReferenceEquals(other, textEntity)) continue;
                other.RecordGraphicsModified(true);
                try
                {
                    var otherBounds = other.GeometricExtents;
                    if (BoundsIntersect2D(myBounds, otherBounds, padding))
                        overlappingHandles.Add(other.Handle.ToString());
                }
                catch { }
            }

            if (overlappingHandles.Count == 0) return false;

            Log.Information("Entity {Handle}: overlaps with {Count} other translation(s): [{Handles}]",
                textEntity.Handle, overlappingHandles.Count, string.Join(", ", overlappingHandles));

            // Binary-search for the largest height that avoids all overlaps
            double low = minHeight;
            double high = searchMax;
            double best = searchMax;
            bool resolved = false;

            for (int i = 0; i < BinarySearchIterations; i++)
            {
                double mid = (low + high) / 2.0;
                SetTextHeight(textEntity, mid);
                textEntity.RecordGraphicsModified(true);

                try
                {
                    var testBounds = textEntity.GeometricExtents;
                    bool stillOverlaps = false;
                    foreach (var other in otherEntities)
                    {
                        if (ReferenceEquals(other, textEntity)) continue;
                        other.RecordGraphicsModified(true);
                        try
                        {
                            if (BoundsIntersect2D(testBounds, other.GeometricExtents, padding))
                            { stillOverlaps = true; break; }
                        }
                        catch { }
                    }
                    if (stillOverlaps) high = mid;
                    else { best = mid; low = mid; resolved = true; }
                }
                catch { high = mid; }
            }

            SetTextHeight(textEntity, best);
            textEntity.RecordGraphicsModified(true);

            if (resolved && best < searchMax)
                Log.Information("Entity {Handle}: overlap resolved {Orig:F2}→{New:F2} (min={Min:F2})",
                    textEntity.Handle, searchMax, best, minHeight);

            return resolved;
        }
        catch (Exception ex)
        {
            Log.Debug("Entity-overlap check failed for {Handle}: {Error}", textEntity.Handle, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Cross-layer collision resolution: binary-searches for the largest text height
    /// that avoids overlapping with nearby geometry from ANY layer (lines, polylines,
    /// hatches, text, etc.).
    ///
    /// Parameters:
    ///   currentHeight     — the entity's CURRENT height (upper bound for binary search)
    ///   trueOriginalHeight — the TRUE un-scaled original height (floor = ratio * this)
    ///
    /// NEVER displaces text — only scales height. Preserves original insertion point.
    /// </summary>
    /// <returns>true if text was scaled to resolve collisions</returns>
    public static bool TryResolveCrossLayerCollisions(
        Entity textEntity,
        BlockTableRecord btr,
        Transaction tr,
        double currentHeight,
        double trueOriginalHeight,
        double minHeightRatio = 0.65,
        Database? db = null)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);

            Extents3d textBounds;
            try { textBounds = textEntity.GeometricExtents; }
            catch { return true; } // Degenerate — skip

            double padding = trueOriginalHeight * 0.15;
            double minHeight = trueOriginalHeight * minHeightRatio;
            double searchMax = Math.Max(currentHeight, minHeight);

            var colliders = new List<Extents3d>();
            var colliderPositions = new List<Point3d>();
            var colliderTypes = new List<string>();
            CollectPotentialCollidersWithTypes(tr, btr, textEntity.ObjectId, textBounds, padding,
                colliders, colliderPositions, colliderTypes, 0);

            // Cross-block scan
            if (colliders.Count == 0 && db != null)
            {
                CollectCrossBlockCollidersWithTypes(tr, db, textEntity.ObjectId, textBounds, padding,
                    colliders, colliderPositions, colliderTypes);
            }

            if (colliders.Count == 0)
            {
                Log.Debug("Entity {Handle}: no cross-layer colliders found (padding={Pad:F1})",
                    textEntity.Handle, padding);
                return false;
            }

            // Log what we found for debugging
            var colliderSummary = colliderTypes
                .GroupBy(t => t)
                .Select(g => $"{g.Count()}×{g.Key}");
            Log.Information("Entity {Handle}: {Count} cross-layer colliders: [{Summary}] (padding={Pad:F1})",
                textEntity.Handle, colliders.Count, string.Join(", ", colliderSummary), padding);

            // Binary-search for the largest height that avoids all collisions
            double low = minHeight;
            double high = searchMax;
            double best = searchMax;
            bool resolved = false;

            for (int i = 0; i < BinarySearchIterations; i++)
            {
                double mid = (low + high) / 2.0;
                SetTextHeight(textEntity, mid);
                textEntity.RecordGraphicsModified(true);

                try
                {
                    var testBounds = textEntity.GeometricExtents;
                    bool stillCollides = false;
                    foreach (var c in colliders)
                    {
                        if (BoundsIntersect2D(testBounds, c, padding))
                        { stillCollides = true; break; }
                    }
                    if (stillCollides) high = mid;
                    else { best = mid; low = mid; resolved = true; }
                }
                catch { high = mid; }
            }

            SetTextHeight(textEntity, best);
            textEntity.RecordGraphicsModified(true);

            if (resolved && best < searchMax)
                Log.Information("Entity {Handle}: cross-layer resolved {Orig:F2}→{New:F2} (floor={Floor:F2})",
                    textEntity.Handle, searchMax, best, minHeight);
            else if (!resolved)
                Log.Warning("Entity {Handle}: cross-layer UNRESOLVED at floor={Floor:F2} ({Count} colliders remain)",
                    textEntity.Handle, minHeight, colliders.Count);

            return resolved;
        }
        catch (Exception ex)
        {
            Log.Debug("Cross-layer collision check failed for {Handle}: {Error}", textEntity.Handle, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Like CollectPotentialColliders but also tracks entity type names for diagnostic logging.
    /// </summary>
    private static void CollectPotentialCollidersWithTypes(
        Transaction tr, BlockTableRecord btr, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> positions, List<string> types, int depth)
    {
        const int maxDepth = 5;
        if (depth > maxDepth) return;

        foreach (ObjectId otherId in btr)
        {
            if (otherId == skipId || !otherId.IsValid) continue;

            Entity? other;
            try { other = tr.GetObject(otherId, OpenMode.ForRead, false) as Entity; }
            catch { continue; }
            if (other == null) continue;

            if (other is BlockReference nestedBr)
            {
                try
                {
                    var nestedBtr = (BlockTableRecord)tr.GetObject(nestedBr.BlockTableRecord, OpenMode.ForRead);
                    CollectPotentialCollidersWithTypes(tr, nestedBtr, skipId, textBounds, padding,
                        colliders, positions, types, depth + 1);
                }
                catch { }
                continue;
            }

            if (other is Dimension) continue;
            if (other is Viewport) continue;

            try
            {
                var otherBounds = other.GeometricExtents;
                if (BoundsIntersect2D(textBounds, otherBounds, padding))
                {
                    colliders.Add(otherBounds);
                    positions.Add(new Point3d(
                        (otherBounds.MinPoint.X + otherBounds.MaxPoint.X) / 2,
                        (otherBounds.MinPoint.Y + otherBounds.MaxPoint.Y) / 2, 0));
                    types.Add(other.GetType().Name);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Like CollectCrossBlockColliders but also tracks entity type names.
    /// </summary>
    private static void CollectCrossBlockCollidersWithTypes(
        Transaction tr, Database db, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> positions, List<string> types)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in bt)
        {
            BlockTableRecord btr;
            try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
            catch { continue; }

            if (btr.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
            if (btr.IsAnonymous) continue;
            if (btr.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;

            CollectPotentialCollidersWithTypes(tr, btr, skipId, textBounds, padding,
                colliders, positions, types, 0);
        }
    }
}
