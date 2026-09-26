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
        { "simsun.ttc", "Arial" },
        { "simsun.ttf", "Arial" },
        { "simhei.ttf", "Arial" },
        { "msyh.ttc", "Arial" },
        { "msyh.ttf", "Arial" },
        { "fangsong", "Arial" },
        { "kaiti", "Arial" },
        { "楷体", "Arial" },
        { "仿宋", "Arial" },
        { "宋体", "Arial" },
        { "黑体", "Arial" },
        // A CJK SHX/big-font is a source-font hint, not a request to render the
        // English translation in a single-stroke drafting font. Use a real TTF.
        { "gbcbig.shx", "Arial" },
        { "gbenor.shx", "Arial" },
        { "hztxt.shx", "Arial" },
        { "hzfs.shx", "Arial" },
        { "Times", "Arial" },
    };

    private static readonly Dictionary<string, string> LatinToCjkFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Arial", "SimHei" },
        { "Helvetica", "SimHei" },
        { "Times", "SimHei" },
        { "simplex.shx", "gbcbig.shx" },
        { "txt.shx", "SimHei" },
        { "romans.shx", "SimHei" },
        { "romand.shx", "SimHei" },
    };

    /// <summary>
    /// Maps a font/style name from source language to target language.
    /// Returns null if no mapping is needed.
    /// </summary>
    public static string? MapFontName(string currentStyleName, bool targetIsCjk)
        => MapFontName(currentStyleName, null, null, targetIsCjk);

    /// <summary>
    /// Maps from the style's actual CAD font files as well as its user-defined style name.
    /// Real drawings commonly call the style "Standard" or "T1" while the font is stored
    /// separately in Filename/BigFontFilename; looking at the style name alone misses that.
    /// </summary>
    public static string? MapFontName(string currentStyleName, string? fontFileName,
        string? bigFontFileName, bool targetIsCjk)
    {
        if (string.IsNullOrEmpty(currentStyleName)) return null;

        var source = string.Join("|", currentStyleName, fontFileName ?? string.Empty,
            bigFontFileName ?? string.Empty);

        if (targetIsCjk)
        {
            foreach (var kv in LatinToCjkFonts)
            {
                if (source.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }
        }
        else
        {
            foreach (var kv in CjkToLatinFonts)
            {
                if (source.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }
        }
        return null;
    }

    /// <summary>Explicit CAD font-file reference for styles created by the offline writer.</summary>
    public static string FontFileName(string mappedFontName) => mappedFontName switch
    {
        "Arial" => "arial.ttf",
        "SimHei" => "simhei.ttf",
        "simplex.shx" => "simplex.shx",
        "gbcbig.shx" => "gbcbig.shx",
        _ => mappedFontName
    };

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
