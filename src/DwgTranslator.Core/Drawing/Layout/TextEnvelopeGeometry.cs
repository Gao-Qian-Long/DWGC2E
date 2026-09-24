namespace DwgTranslator.Core.Services;

/// <summary>CAD-independent geometry. Keep entity transforms and bounds refresh in the host.</summary>
public static class TextEnvelopeGeometry
{
    public static double FittingAxisOffset(double min, double max, double originalMin, double originalMax)
    {
        if (max - min <= originalMax - originalMin)
            return min < originalMin ? originalMin - min : max > originalMax ? originalMax - max : 0;
        return 0;
    }

    public static bool IsAxisInside(double min, double max, double originalMin, double originalMax,
        double tolerance = 0.01)
        => min >= originalMin - tolerance && max <= originalMax + tolerance;

    /// <summary>Shortest 2D distance between two axis-aligned rectangles; overlapping bounds return zero.</summary>
    public static double AxisAlignedDistance2D(
        double aMinX, double aMinY, double aMaxX, double aMaxY,
        double bMinX, double bMinY, double bMaxX, double bMaxY)
    {
        double dx = Math.Max(0, Math.Max(bMinX - aMaxX, aMinX - bMaxX));
        double dy = Math.Max(0, Math.Max(bMinY - aMaxY, aMinY - bMaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static bool ViolatesClearance(double measuredDistance, double requiredDistance, double tolerance = 0.01)
        => measuredDistance < Math.Max(0, requiredDistance - tolerance);

    public static double NormalizeHalfTurn(double rotation)
    {
        double value = rotation % System.Math.PI;
        return value < 0 ? value + System.Math.PI : value;
    }
}
