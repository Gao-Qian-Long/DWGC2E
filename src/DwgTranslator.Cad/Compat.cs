namespace DwgTranslator.Cad;

internal static class Compat
{
    public static double Clamp(double value, double min, double max)
    {
        if (value < min) return min;
        return value > max ? max : value;
    }
}
