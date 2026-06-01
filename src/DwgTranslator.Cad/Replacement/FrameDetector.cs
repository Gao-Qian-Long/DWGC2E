using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Cad;

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

    private const int MaxBlockRecursionDepth = 5;

    /// <summary>
    /// Detects all candidate frame boundaries in the given database.
    /// Searches ModelSpace, all PaperSpace layouts, and recursively scans
    /// BlockReference (INSERT) entities — which is how 85-95% of real-world
    /// DWG files store their drawing frames / title blocks.
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
                    DetectFramesInBlock(btr, tr, frames, Matrix3d.Identity, 0);
                }
            }
            tr.Commit();
        }
        Log.Information("FrameDetector: found {Count} frame(s)", frames.Count);
        return frames;
    }

    /// <summary>
    /// Recursively scans a block table record for frame-like polylines.
    /// When a BlockReference is encountered, its block definition is scanned
    /// recursively and any frames found are transformed to world coordinates.
    /// </summary>
    /// <param name="btr">Block table record to scan.</param>
    /// <param name="tr">Active transaction.</param>
    /// <param name="frames">Output list of detected frame extents (in WCS).</param>
    /// <param name="parentTransform">Cumulative transform from parent block references.</param>
    /// <param name="depth">Current recursion depth (capped at MaxBlockRecursionDepth).</param>
    private static void DetectFramesInBlock(BlockTableRecord btr, Transaction tr,
        List<Extents3d> frames, Matrix3d parentTransform, int depth)
    {
        if (depth > MaxBlockRecursionDepth) return;

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
            if (entity == null) continue;

            // ── NEW: Recurse into BlockReference to find frame polylines
            //    hidden inside block definitions (the standard pattern in
            //    professional DWG files where title blocks are INSERT entities).
            if (entity is BlockReference br)
            {
                try
                {
                    var nestedBtr = (BlockTableRecord)tr.GetObject(
                        br.BlockTableRecord, OpenMode.ForRead);
                    if (nestedBtr != null && !nestedBtr.IsAnonymous)
                    {
                        // Combine transforms: parent → this BlockReference → nested
                        var combined = br.BlockTransform.PreMultiplyBy(parentTransform);
                        DetectFramesInBlock(nestedBtr, tr, frames, combined, depth + 1);
                    }
                }
                catch { /* skip inaccessible block references */ }
                continue;
            }

            // Check for frame polylines at this level
            Extents3d? frameExt = null;

            if (entity is Polyline poly && IsRectangularFrame(poly))
            {
                frameExt = poly.GeometricExtents;
            }
            else if (entity is Polyline2d poly2d && IsRectangularFrame(poly2d))
            {
                frameExt = poly2d.GeometricExtents;
            }

            if (frameExt.HasValue)
            {
                var ext = frameExt.Value;
                double area = (ext.MaxPoint.X - ext.MinPoint.X) * (ext.MaxPoint.Y - ext.MinPoint.Y);
                if (area >= MinFrameArea && area <= MaxFrameArea)
                {
                    // Apply cumulative block reference transform to convert
                    // from block-local coordinates to world coordinates.
                    if (parentTransform != Matrix3d.Identity)
                    {
                        // Transform all 4 corners and compute the new AABB
                        // (handles rotation, scaling, and non-uniform scaling).
                        var corners = new[]
                        {
                            ext.MinPoint,
                            ext.MaxPoint,
                            new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, 0),
                            new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, 0),
                        };
                        var transformed = new Extents3d();
                        foreach (var corner in corners)
                            transformed.AddPoint(corner.TransformBy(parentTransform));
                        frames.Add(transformed);
                    }
                    else
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

        // Collect vertices from the Polyline2d (which may use different vertex types).
        var pts = new List<Point3d>();
        foreach (ObjectId vid in poly2d)
        {
            try
            {
                var vtx = (Vertex2d)vid.GetObject(OpenMode.ForRead);
                pts.Add(vtx.Position);
            }
            catch { return false; }
        }

        int n = pts.Count;
        if (n < 4 || n > 8) return false; // Match Polyline path: 4-8 vertices

        // Verify all angles are approximately 90 degrees
        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var v1 = curr - prev;
            var v2 = next - curr;

            if (v1.Length < 0.001 || v2.Length < 0.001) continue;

            double dot = Math.Abs(v1.X * v2.X + v1.Y * v2.Y + v1.Z * v2.Z)
                       / (v1.Length * v2.Length);
            if (dot > 0.15) return false; // Not close to 90 degrees
        }

        // Verify aspect ratio (additional safety check)
        var ext = poly2d.GeometricExtents;
        double w = ext.MaxPoint.X - ext.MinPoint.X;
        double h = ext.MaxPoint.Y - ext.MinPoint.Y;
        if (w <= 0 || h <= 0) return false;
        double aspect = Math.Max(w, h) / Math.Min(w, h);
        return aspect >= 1.1 && aspect <= 2.5;
    }
}
