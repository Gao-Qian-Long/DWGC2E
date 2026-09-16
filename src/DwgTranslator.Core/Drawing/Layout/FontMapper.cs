namespace DwgTranslator.Core.Services;

/// <summary>
/// Shared font mapping logic for CJK↔English font conversion.
/// Used by all writeback paths (offline ACadSharp, online AcadWriterEngine, interactive TextReplacer).
/// </summary>
public static class FontMapper
{
    public static string MapInlineFonts(string content, bool targetIsCjk)
    {
        return System.Text.RegularExpressions.Regex.Replace(content, @"\\[fF]([^;]+);", match =>
        {
            var fields=match.Groups[1].Value.Split('|');
            var mapped=MapFontName(fields[0],targetIsCjk);
            if(mapped==null)return match.Value;
            // Keep bold/italic, but do not carry a CJK charset or font-family
            // classification into the replacement Latin font.
            var emphasis=string.Join("",fields.Skip(1).Where(x=>x.StartsWith("b") || x.StartsWith("i")).Select(x=>"|"+x));
            return "\\f"+mapped+emphasis+";";
        });
    }
    private static readonly Dictionary<string, string> CjkToLatinFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "SimHei", "Arial" },
        { "SimSun", "Arial" },
        { "宋体", "Arial" },
        { "黑体", "Arial" },
        { "gbcbig.shx", "simplex.shx" },
        { "Times", "Arial" },
    };

    private static readonly Dictionary<string, string> LatinToCjkFonts = new(StringComparer.OrdinalIgnoreCase)
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
    public static string? MapFontName(string currentStyleName, bool targetIsCjk)
    {
        if (string.IsNullOrEmpty(currentStyleName)) return null;

        if (targetIsCjk)
        {
            foreach (var kv in LatinToCjkFonts)
            {
                if (currentStyleName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }
        }
        else
        {
            foreach (var kv in CjkToLatinFonts)
            {
                if (currentStyleName.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
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
        return styleName.IndexOf("SimHei", StringComparison.OrdinalIgnoreCase) >= 0
            || styleName.IndexOf("SimSun", StringComparison.OrdinalIgnoreCase) >= 0
            || styleName.IndexOf("宋体", StringComparison.OrdinalIgnoreCase) >= 0
            || styleName.IndexOf("黑体", StringComparison.OrdinalIgnoreCase) >= 0
            || styleName.IndexOf("gbcbig", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
