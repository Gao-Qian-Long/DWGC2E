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
    /// layouts, AND block definitions (for frames stored as INSERT/CadInsert entities).
    /// Accepts closed LwPolylines with 4-8 vertices (allows chamfered corners) and
    /// validates approximate rectangular shape via angle checks.
    /// </summary>
    public static List<(double minX, double minY, double maxX, double maxY)> DetectFrames(CadDocument doc)
    {
        var frames = new List<(double, double, double, double)>();
        try
        {
            var collectionsToScan = new List<(IEnumerable<CadEntity> entities, string source)>();

            collectionsToScan.Add((doc.ModelSpace.Entities, "ModelSpace"));

            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock?.Entities != null)
                    collectionsToScan.Add((layout.AssociatedBlock.Entities, $"Layout:{layout.Name}"));
            }

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
    /// Recursively enters CadInsert entities to find frames inside block definitions.
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

                    var worldBounds = CadGeometryHelper.TransformAabb(
                        minX, minY, maxX, maxY,
                        scaleX, scaleY, cosR, sinR,
                        insertX, insertY);

                    frames.Add(worldBounds);
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
        CheckAndScaleToFitFrameCore(
            textEntity, frames, originalHeight,
            e => (e.InsertPoint.X, e.InsertPoint.Y),
            DwgBoundsEstimator.EstimateTextBounds,
            e => e.Height,
            (e, h) => e.Height = h,
            e => e.Handle);
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
        CheckAndScaleToFitFrameCore(
            mtext, frames, originalHeight,
            e => (e.InsertPoint.X, e.InsertPoint.Y),
            DwgBoundsEstimator.EstimateMTextBounds,
            e => e.Height,
            (e, h) => e.Height = h,
            e => e.Handle);
    }

    /// <summary>
    /// Generic core for frame-boundary scaling. Eliminates duplication between
    /// CadText and CadMText overloads.
    /// </summary>
    private static void CheckAndScaleToFitFrameCore<T>(
        T entity,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        double originalHeight,
        Func<T, (double X, double Y)> getInsertPoint,
        Func<T, (double minX, double minY, double maxX, double maxY)> getBounds,
        Func<T, double> getHeight,
        Action<T, double> setHeight,
        Func<T, ulong> getHandle)
    {
        try
        {
            var (px, py) = getInsertPoint(entity);
            var frame = FindClosestFrame(px, py, frames);
            if (!frame.HasValue) return;

            var bounds = getBounds(entity);
            if (!ExceedsFrame(bounds, frame.Value)) return;

            double frameW = frame.Value.maxX - frame.Value.minX;
            double frameH = frame.Value.maxY - frame.Value.minY;
            double textW = bounds.maxX - bounds.minX;
            double textH = bounds.maxY - bounds.minY;

            double scale = ComputeFrameScale(frameW, frameH, textW, textH);

            if (scale < 1.0)
            {
                double currentHeight = getHeight(entity);
                double newHeight = currentHeight * scale;
                double minHeight = originalHeight * 0.4;
                if (newHeight < minHeight) newHeight = minHeight;
                setHeight(entity, newHeight);
                Log.Debug("Frame-boundary scaling: {Type} {Handle} scaled by {Scale:F2} (height {Old:F2} -> {New:F2})",
                    typeof(T).Name, getHandle(entity), scale, originalHeight, newHeight);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame boundary check failed for {Type} {Handle} (non-fatal)",
                typeof(T).Name, getHandle(entity));
        }
    }
}
