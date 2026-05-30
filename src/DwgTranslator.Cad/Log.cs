namespace DwgTranslator.Cad;

/// <summary>
/// Lightweight logging stub for the CAD plugin.
/// Replaces Serilog to avoid assembly binding conflicts with AutoCAD's own Serilog.
/// All output goes to the Visual Studio / IDE debug output.
/// </summary>
public static class Log
{
    public static void Information(string message, params object?[] args) { }
    public static void Warning(string message, params object?[] args) { }
    public static void Warning(Exception? ex, string message, params object?[] args) { }
    public static void Error(string message, params object?[] args) { }
    public static void Error(Exception? ex, string message, params object?[] args) { }
    public static void Debug(string message, params object?[] args) { }
}
