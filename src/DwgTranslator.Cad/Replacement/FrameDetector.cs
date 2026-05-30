using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Serilog;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Detects drawing frame boundaries (closed rectangular polylines) in a DWG database.
/// </summary>
public static class FrameDetector
{
    /// <summary>
    /// Minimum area (in drawing units squared) for a polyline to be considered a frame.
    /// Filters out small decorative rectangles.
    /// </summary>
    public static double MinFrameArea { get; set; } = 10000.0;

    /// <summary>
    /// Maximum area (in drawing units squared) to filter out oversized false positives.
    /// </summary>
    public static double MaxFrameArea { get; set; } = 1e12;

    /// <summary>
    /// Detects all candidate frame boundaries in the given database.
    /// Searches ModelSpace and all PaperSpace layouts.
    /// </summary>
    public static List<Extents3d> DetectFrames(Database db)
    {
        var frames = new List<Extents3d>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                // Skip anonymous and standard internal blocks
                if (btr.IsAnonymous) continue;
                if (btr.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) ||
                    btr.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase) ||
                    (!btr.IsLayout && !btr.Name.StartsWith("*")))
                {
                    DetectFramesInBlock(btr, tr, frames);
                }
            }
            tr.Commit();
        }
        Log.Information("FrameDetector: found {Count} frame(s)", frames.Count);
        return frames;
    }

    private static void DetectFramesInBlock(BlockTableRecord btr, Transaction tr, List<Extents3d> frames)
    {
        foreach (ObjectId entId in btr)
        {
            Entity? entity = null;
            try
            {
                entity = tr.GetObject(entId, OpenMode.ForRead) as Entity;
            }
            catch
            {
                continue;
            }

            if (entity is Polyline poly)
            {
                if (IsRectangularFrame(poly))
                {
                    var ext = poly.GeometricExtents;
                    double area = (ext.MaxPoint.X - ext.MinPoint.X) * (ext.MaxPoint.Y - ext.MinPoint.Y);
                    if (area >= MinFrameArea && area <= MaxFrameArea)
                    {
                        frames.Add(ext);
                    }
                }
            }
            else if (entity is Polyline2d poly2d)
            {
                if (IsRectangularFrame(poly2d))
                {
                    var ext = poly2d.GeometricExtents;
                    double area = (ext.MaxPoint.X - ext.MinPoint.X) * (ext.MaxPoint.Y - ext.MinPoint.Y);
                    if (area >= MinFrameArea && area <= MaxFrameArea)
                    {
                        frames.Add(ext);
                    }
                }
            }
        }
    }

    private static bool IsRectangularFrame(Polyline poly)
    {
        if (!poly.Closed) return false;
        int n = poly.NumberOfVertices;
        if (n < 4 || n > 8) return false; // allow chamfered corners up to 8 vertices

        // Collect vertices
        var pts = new Point2d[n];
        for (int i = 0; i < n; i++) pts[i] = poly.GetPoint2dAt(i);

        // Check if all angles are approximately 90 degrees (rectangle or rounded rectangle)
        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var v1 = curr - prev;
            var v2 = next - curr;

            // Skip very short segments (likely fillet/chamfer)
            if (v1.Length < 0.001 || v2.Length < 0.001) continue;

            double dot = (v1.X * v2.X + v1.Y * v2.Y) / (v1.Length * v2.Length);
            if (Math.Abs(dot) > 0.15) // not close to 90 degrees
                return false;
        }
        return true;
    }

    private static bool IsRectangularFrame(Polyline2d poly2d)
    {
        if (!poly2d.Closed) return false;
        // Polyline2d is less common; use geometric extent aspect ratio as heuristic
        var ext = poly2d.GeometricExtents;
        double w = ext.MaxPoint.X - ext.MinPoint.X;
        double h = ext.MaxPoint.Y - ext.MinPoint.Y;
        if (w <= 0 || h <= 0) return false;
        double aspect = Math.Max(w, h) / Math.Min(w, h);
        // Typical drawing frame aspect ratios: A4~A0 range from 1.2 to 1.7
        return aspect >= 1.1 && aspect <= 2.5;
    }
}
