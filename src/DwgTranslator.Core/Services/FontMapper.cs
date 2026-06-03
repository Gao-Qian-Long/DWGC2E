namespace DwgTranslator.Core.Services;

/// <summary>
/// Shared font mapping logic for CJK↔English font conversion.
/// Used by all writeback paths (offline ACadSharp, online AcadWriterEngine, interactive TextReplacer).
/// </summary>
public static class FontMapper
{
    private static readonly Dictionary<string, string> CnToEnFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "SimHei", "Arial" },
        { "SimSun", "Arial" },
        { "宋体", "Arial" },
        { "黑体", "Arial" },
        { "gbcbig.shx", "simplex.shx" },
        { "Times", "Arial" },
    };

    private static readonly Dictionary<string, string> EnToCnFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Arial", "SimHei" },
        { "Helvetica", "SimHei" },
        { "Times", "SimHei" },
        { "simplex.shx", "gbcbig.shx" },
    };

    /// <summary>
    /// Maps a font/style name from source language to target language.
    /// Returns null if no mapping is needed.
    /// </summary>
    public static string? MapFontName(string currentStyleName, bool cnToEn)
    {
        if (string.IsNullOrEmpty(currentStyleName)) return null;

        if (cnToEn)
        {
            foreach (var kv in CnToEnFonts)
            {
                if (currentStyleName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }
        else
        {
            foreach (var kv in EnToCnFonts)
            {
                if (currentStyleName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Checks if a style name represents a CJK font.
    /// </summary>
    public static bool IsCjkFont(string styleName)
    {
        if (string.IsNullOrEmpty(styleName)) return false;
        return styleName.Contains("SimHei", StringComparison.OrdinalIgnoreCase)
            || styleName.Contains("SimSun", StringComparison.OrdinalIgnoreCase)
            || styleName.Contains("宋体", StringComparison.OrdinalIgnoreCase)
            || styleName.Contains("黑体", StringComparison.OrdinalIgnoreCase)
            || styleName.Contains("gbcbig", StringComparison.OrdinalIgnoreCase);
    }
}
