using ACadSharp;
using ACadSharp.Entities;
using Serilog;

using CadEntity = ACadSharp.Entities.Entity;
using CadInsert = ACadSharp.Entities.Insert;
using CadLwPolyline = ACadSharp.Entities.LwPolyline;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Detects rectangular frame boundaries in CAD drawings by scanning
/// ModelSpace, PaperSpace layouts, and block definitions for closed
/// LwPolylines that form approximate rectangles.
///
/// Also handles frame-boundary-based text scaling: when translated text
/// exceeds its nearest frame boundary, scales it down to fit.
/// </summary>
internal static class DwgFrameDetector
{
    private const double MinFrameAreaSquareUnits = 10000;

    /// <summary>
    /// Detects rectangular frame boundaries by scanning ModelSpace, all PaperSpace
    /// layouts, AND block definitions (for frames stored as INSERT/CadInsert entities
    /// — the standard pattern in 85-95% of professional DWG files).
    ///
    /// Accepts closed LwPolylines with 4-8 vertices (allows chamfered corners) and
    /// validates approximate rectangular shape via angle checks.
    /// Recursively scans CadInsert entities to find frame polylines nested inside block
    /// definitions, transforming bounds to world coordinates.
    /// </summary>
    public static List<(double minX, double minY, double maxX, double maxY)> DetectFrames(CadDocument doc)
    {
        var frames = new List<(double, double, double, double)>();
        try
        {
            // Collect all entity collections to scan:
            // (a) ModelSpace, (b) PaperSpace layouts, (c) user-defined block records
            var collectionsToScan = new List<(IEnumerable<CadEntity> entities, string source)>();

            collectionsToScan.Add((doc.ModelSpace.Entities, "ModelSpace"));

            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock?.Entities != null)
                    collectionsToScan.Add((layout.AssociatedBlock.Entities, $"Layout:{layout.Name}"));
            }

            // Also scan user-defined block records for frame polylines that may be
            // referenced by CadInsert entities in ModelSpace/PaperSpace.
            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Entities != null && blockRecord.Entities.Count > 0)
                    collectionsToScan.Add((blockRecord.Entities, $"Block:{blockRecord.Name}"));
            }

            foreach (var (entities, source) in collectionsToScan)
            {
                ScanEntitiesForFrames(entities, frames, doc, 0);
            }

            if (frames.Count > 0)
                Log.Information("DetectFrames: found {Count} frame(s) across {Sources} source(s)",
                    frames.Count, collectionsToScan.Count);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame detection encountered an error (non-fatal)");
        }
        return frames;
    }

    /// <summary>
    /// Scans a collection of entities for frame-like closed LwPolylines.
    /// Recursively enters CadInsert entities to find frames inside block definitions
    /// (up to maxDepth=5 to prevent infinite recursion from circular references).
    /// </summary>
    private static void ScanEntitiesForFrames(
        IEnumerable<CadEntity> entities,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        CadDocument doc,
        int depth,
        double insertX = 0, double insertY = 0,
        double scaleX = 1, double scaleY = 1,
        double rotation = 0)
    {
        const int maxDepth = 5;
        if (depth > maxDepth) return;

        foreach (var entity in entities)
        {
            if (entity == null) continue;

            // ── Recurse into CadInsert (BlockReference) ──
            if (entity is CadInsert insert)
            {
                var blockDef = doc.BlockRecords.FirstOrDefault(
                    b => string.Equals(b.Name, insert.Block?.Name, StringComparison.OrdinalIgnoreCase));
                if (blockDef != null && blockDef.Entities != null)
                {
                    double cosR = Math.Cos(insert.Rotation);
                    double sinR = Math.Sin(insert.Rotation);
                    double newX = insertX + insert.InsertPoint.X * scaleX;
                    double newY = insertY + insert.InsertPoint.Y * scaleY;
                    double newSx = scaleX * insert.XScale;
                    double newSy = scaleY * insert.YScale;
                    double newRot = rotation + insert.Rotation;

                    ScanEntitiesForFrames(blockDef.Entities, frames, doc,
                        depth + 1, newX, newY, newSx, newSy, newRot);
                }
                continue;
            }

            // ── Check for frame LwPolyline ──
            if (entity is CadLwPolyline poly && poly.IsClosed)
            {
                int n = poly.Vertices.Count;
                if (n < 4 || n > 8) continue;

                if (!IsApproximatelyRectangular(poly)) continue;

                double minX = poly.Vertices.Min(v => v.Location.X);
                double minY = poly.Vertices.Min(v => v.Location.Y);
                double maxX = poly.Vertices.Max(v => v.Location.X);
                double maxY = poly.Vertices.Max(v => v.Location.Y);
                double area = (maxX - minX) * (maxY - minY);

                if (area <= MinFrameAreaSquareUnits) continue;

                // Apply accumulated CadInsert transform to convert from
                // block-local coordinates to world coordinates.
                if (depth > 0)
                {
                    double cosR = Math.Cos(rotation);
                    double sinR = Math.Sin(rotation);

                    (double x, double y) TransformCorner(double x, double y)
                    {
                        double sx = x * scaleX;
                        double sy = y * scaleY;
                        double rx = sx * cosR - sy * sinR;
                        double ry = sx * sinR + sy * cosR;
                        return (rx + insertX, ry + insertY);
                    }

                    var c1 = TransformCorner(minX, minY);
                    var c2 = TransformCorner(maxX, maxY);
                    var c3 = TransformCorner(minX, maxY);
                    var c4 = TransformCorner(maxX, minY);

                    double wMinX = Math.Min(Math.Min(c1.x, c2.x), Math.Min(c3.x, c4.x));
                    double wMinY = Math.Min(Math.Min(c1.y, c2.y), Math.Min(c3.y, c4.y));
                    double wMaxX = Math.Max(Math.Max(c1.x, c2.x), Math.Max(c3.x, c4.x));
                    double wMaxY = Math.Max(Math.Max(c1.y, c2.y), Math.Max(c3.y, c4.y));

                    frames.Add((wMinX, wMinY, wMaxX, wMaxY));
                }
                else
                {
                    frames.Add((minX, minY, maxX, maxY));
                }
            }
        }
    }

    /// <summary>
    /// Quick rectangular validation for LwPolylines: checks that all interior
    /// angles are approximately 90 degrees (dot product &lt; 0.15 threshold).
    /// </summary>
    private static bool IsApproximatelyRectangular(CadLwPolyline poly)
    {
        int n = poly.Vertices.Count;
        if (n < 4) return false;

        for (int i = 0; i < n; i++)
        {
            var prev = poly.Vertices[(i - 1 + n) % n].Location;
            var curr = poly.Vertices[i].Location;
            var next = poly.Vertices[(i + 1) % n].Location;

            double v1x = curr.X - prev.X;
            double v1y = curr.Y - prev.Y;
            double v2x = next.X - curr.X;
            double v2y = next.Y - curr.Y;

            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);

            if (len1 < 0.001 || len2 < 0.001) continue;

            double dot = Math.Abs(v1x * v2x + v1y * v2y) / (len1 * len2);
            if (dot > 0.15) return false;
        }
        return true;
    }

    /// <summary>
    /// Finds the frame whose center is closest to the given point.
    /// Returns null if no frames are available.
    /// </summary>
    public static (double minX, double minY, double maxX, double maxY)? FindClosestFrame(
        double px, double py,
        List<(double minX, double minY, double maxX, double maxY)> frames)
    {
        if (frames.Count == 0) return null;
        return frames.OrderBy(f =>
            Math.Pow((f.minX + f.maxX) / 2 - px, 2) +
            Math.Pow((f.minY + f.maxY) / 2 - py, 2)).First();
    }

    /// <summary>
    /// Computes the scale factor required to fit text bounds within a frame boundary.
    /// Returns a value in [0.4, 1.0].
    /// </summary>
    private static double ComputeFrameScale(double frameW, double frameH, double textW, double textH)
    {
        double scaleW = textW > 0 ? Math.Max(0.4, (frameW * 0.95) / textW) : 1.0;
        double scaleH = textH > 0 ? Math.Max(0.4, (frameH * 0.95) / textH) : 1.0;
        return Math.Min(1.0, Math.Min(scaleW, scaleH));
    }

    /// <summary>
    /// Returns true if the text bounding box exceeds the frame boundaries.
    /// Uses a capped tolerance: 0.5% of frame dimension, but at most 3.0 units
    /// and at least 0.5 unit.
    /// </summary>
    private static bool ExceedsFrame(
        (double minX, double minY, double maxX, double maxY) bounds,
        (double minX, double minY, double maxX, double maxY) frame)
    {
        double frameW = frame.maxX - frame.minX;
        double frameH = frame.maxY - frame.minY;
        double tolX = Math.Clamp(frameW * 0.005, 0.5, 3.0);
        double tolY = Math.Clamp(frameH * 0.005, 0.5, 3.0);
        return bounds.minX < frame.minX - tolX
            || bounds.maxX > frame.maxX + tolX
            || bounds.minY < frame.minY - tolY
            || bounds.maxY > frame.maxY + tolY;
    }

    /// <summary>
    /// Checks whether a CadText entity's estimated bounds exceed its closest frame boundary.
    /// If so, scales down the text height to fit within the frame (down to 40% of original).
    /// </summary>
    public static void CheckAndScaleToFitFrame(
        CadText textEntity,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        double originalHeight)
    {
        try
        {
            double px = textEntity.InsertPoint.X;
            double py = textEntity.InsertPoint.Y;
            var frame = FindClosestFrame(px, py, frames);
            if (!frame.HasValue) return;

            var bounds = DwgBoundsEstimator.EstimateTextBounds(textEntity);
            if (!ExceedsFrame(bounds, frame.Value)) return;

            double frameW = frame.Value.maxX - frame.Value.minX;
            double frameH = frame.Value.maxY - frame.Value.minY;
            double textW = bounds.maxX - bounds.minX;
            double textH = bounds.maxY - bounds.minY;

            double scale = ComputeFrameScale(frameW, frameH, textW, textH);

            if (scale < 1.0)
            {
                double newHeight = textEntity.Height * scale;
                double minHeight = originalHeight * 0.4;
                if (newHeight < minHeight) newHeight = minHeight;
                textEntity.Height = newHeight;
                Log.Debug("Frame-boundary scaling: Text {Handle} scaled by {Scale:F2} (height {Old:F2} -> {New:F2})",
                    textEntity.Handle, scale, originalHeight, newHeight);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame boundary check failed for text {Handle} (non-fatal)", textEntity.Handle);
        }
    }

    /// <summary>
    /// Checks whether an MText entity's estimated bounds exceed its closest frame boundary.
    /// If so, scales down the text height to fit within the frame (down to 40% of original).
    /// </summary>
    public static void CheckAndScaleToFitFrame(
        CadMText mtext,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        double originalHeight)
    {
        try
        {
            double px = mtext.InsertPoint.X;
            double py = mtext.InsertPoint.Y;
            var frame = FindClosestFrame(px, py, frames);
            if (!frame.HasValue) return;

            var bounds = DwgBoundsEstimator.EstimateMTextBounds(mtext);
            if (!ExceedsFrame(bounds, frame.Value)) return;

            double frameW = frame.Value.maxX - frame.Value.minX;
            double frameH = frame.Value.maxY - frame.Value.minY;
            double textW = bounds.maxX - bounds.minX;
            double textH = bounds.maxY - bounds.minY;

            double scale = ComputeFrameScale(frameW, frameH, textW, textH);

            if (scale < 1.0)
            {
                double newHeight = mtext.Height * scale;
                double minHeight = originalHeight * 0.4;
                if (newHeight < minHeight) newHeight = minHeight;
                mtext.Height = newHeight;
                Log.Debug("Frame-boundary scaling: MText {Handle} scaled by {Scale:F2} (height {Old:F2} -> {New:F2})",
                    mtext.Handle, scale, originalHeight, newHeight);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame boundary check failed for MText {Handle} (non-fatal)", mtext.Handle);
        }
    }
}
