using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Provides geometric collision detection between text entities and drawing frames.
/// </summary>
public static class CollisionDetector
{
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
}
