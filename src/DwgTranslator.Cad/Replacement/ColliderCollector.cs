using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Collects potential collision geometry from block table records.
/// Includes proximity filtering and per-type collision padding.
/// </summary>
internal static class ColliderCollector
{
    private const int MaxRecursionDepth = 2;

    /// <summary>
    /// Collects potential collider bounds from a block table record.
    /// Supports DBText, MText, Line, Polyline, Arc, Circle, Spline, Solid, Hatch, Dimension, MLeader, and nested BlockReferences.
    /// </summary>
    public static void CollectPotentialColliders(
        Transaction tr, BlockTableRecord btr, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> colliderPositions, int depth,
        bool trackTypes = false,
        List<string>? types = null, List<double>? typePaddings = null)
    {
        if (depth > MaxRecursionDepth) return;

        double proximityRadius = padding * 3.0;
        double searchMinX = textBounds.MinPoint.X - proximityRadius;
        double searchMaxX = textBounds.MaxPoint.X + proximityRadius;
        double searchMinY = textBounds.MinPoint.Y - proximityRadius;
        double searchMaxY = textBounds.MaxPoint.Y + proximityRadius;

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
                    var insPt = nestedBr.Position;
                    if (insPt.X < searchMinX - 100 || insPt.X > searchMaxX + 100 ||
                        insPt.Y < searchMinY - 100 || insPt.Y > searchMaxY + 100)
                        continue;

                    var nestedBtr = (BlockTableRecord)tr.GetObject(nestedBr.BlockTableRecord, OpenMode.ForRead);
                    CollectPotentialColliders(tr, nestedBtr, skipId, textBounds, padding,
                        colliders, colliderPositions, depth + 1, trackTypes, types, typePaddings);
                }
                catch { }
                continue;
            }

            if (other is Viewport) continue;

            double typePadding = GetCollisionPadding(other, padding);

            try
            {
                var otherBounds = other.GeometricExtents;

                double expandedPadding = typePadding * 2.0;
                if (otherBounds.MaxPoint.X < searchMinX - expandedPadding ||
                    otherBounds.MinPoint.X > searchMaxX + expandedPadding ||
                    otherBounds.MaxPoint.Y < searchMinY - expandedPadding ||
                    otherBounds.MinPoint.Y > searchMaxY + expandedPadding)
                    continue;

                if (CollisionDetector.BoundsIntersect2D(textBounds, otherBounds, typePadding))
                {
                    colliders.Add(otherBounds);
                    colliderPositions.Add(new Point3d(
                        (otherBounds.MinPoint.X + otherBounds.MaxPoint.X) / 2,
                        (otherBounds.MinPoint.Y + otherBounds.MaxPoint.Y) / 2, 0));
                    types?.Add(other.GetType().Name);
                    typePaddings?.Add(typePadding);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Scans ALL block table records for potential cross-block collisions.
    /// Skips model space, anonymous blocks, and paper space layout records.
    /// </summary>
    public static void CollectCrossBlockColliders(
        Transaction tr, Database db, ObjectId skipId,
        Extents3d textBounds, double padding,
        List<Extents3d> colliders, List<Point3d> colliderPositions,
        bool trackTypes = false,
        List<string>? types = null, List<double>? typePaddings = null)
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

            CollectPotentialColliders(tr, btr, skipId, textBounds, padding,
                colliders, colliderPositions, 0, trackTypes, types, typePaddings);
        }
    }

    private static double GetCollisionPadding(Entity entity, double basePadding)
    {
        return entity switch
        {
            DBText or MText or AttributeReference => basePadding * 0.25,
            Line or Polyline or Arc or Circle or Spline or Polyline2d or Polyline3d or MLeader => basePadding * 0.60,
            Solid or Solid3d or Region or Hatch => basePadding * 0.80,
            Dimension => basePadding * 0.40,
            _ => basePadding
        };
    }
}
