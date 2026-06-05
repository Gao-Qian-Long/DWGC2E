using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

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
                    var insPt = nestedBr.Position;
                    if (insPt.X < searchMinX - 100 || insPt.X > searchMaxX + 100 ||
                        insPt.Y < searchMinY - 100 || insPt.Y > searchMaxY + 100)
                        continue;

                    var nestedBtr = (BlockTableRecord)tr.GetObject(nestedBr.BlockTableRecord, OpenMode.ForRead);
                    CollectPotentialColliders(tr, nestedBtr, skipId, textBounds, proximityPadding,
                        result, depth + 1);
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