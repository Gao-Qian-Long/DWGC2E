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

    public static double NormalizeHalfTurn(double rotation)
    {
        double value = rotation % System.Math.PI;
        return value < 0 ? value + System.Math.PI : value;
    }
}
