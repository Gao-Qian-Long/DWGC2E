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

namespace DwgTranslator.Cad.Replacement;

internal static class ColliderCollector
{
    private const int MaxRecursionDepth = 2;

    public static List<Entity> CollectPotentialColliders(
        Transaction tr, BlockTableRecord btr, ObjectId skipId,
        Extents3d textBounds, double proximityPadding)
    {
        var result = new List<Entity>();
        CollectPotentialColliders(tr, btr, skipId, textBounds, proximityPadding, result, 0);
        return result;
    }

    private static void CollectPotentialColliders(
        Transaction tr, BlockTableRecord btr, ObjectId skipId,
        Extents3d textBounds, double proximityPadding,
        List<Entity> result, int depth)
    {
        if (depth > MaxRecursionDepth) return;

        double proximityRadius = proximityPadding * 3.0;
        double searchMinX = textBounds.MinPoint.X - proximityRadius;
        double searchMaxX = textBounds.MaxPoint.X + proximityRadius;
        double searchMinY = textBounds.MinPoint.Y - proximityRadius;
        double searchMaxY = textBounds.MaxPoint.Y + proximityRadius;

        foreach (ObjectId otherId in btr)
        {
            if (otherId == skipId || !otherId.IsValid) continue;

            Entity? other;
            try { other = tr.GetObject(otherId, OpenMode.ForRead, false) as Entity; }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"ColliderCollector: {ex.Message}"); continue; }
            if (other == null) continue;

            if (other is BlockReference nestedBr)
            {
                try
                {
                    // BlockReference.GeometricExtents is expressed in the owning BTR's
                    // coordinate space and includes its insertion/rotation/scale. Do not
                    // recurse into raw block-definition entities, whose extents are local.
                    var blockBounds = nestedBr.GeometricExtents;
                    if (blockBounds.MaxPoint.X < searchMinX || blockBounds.MinPoint.X > searchMaxX ||
                        blockBounds.MaxPoint.Y < searchMinY || blockBounds.MinPoint.Y > searchMaxY)
                        continue;
                    result.Add(nestedBr);
                }
                catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"ColliderCollector: {ex.Message}"); }
                continue;
            }

            if (other is Viewport) continue;

            try
            {
                var otherBounds = other.GeometricExtents;

                if (otherBounds.MaxPoint.X < searchMinX ||
                    otherBounds.MinPoint.X > searchMaxX ||
                    otherBounds.MaxPoint.Y < searchMinY ||
                    otherBounds.MinPoint.Y > searchMaxY)
                    continue;

                result.Add(other);
            }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"ColliderCollector: {ex.Message}"); }
        }
    }
}
