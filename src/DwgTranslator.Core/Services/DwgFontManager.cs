using ACadSharp;
using ACadSharp.Tables;
using Serilog;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Manages font styles in a CAD document for DWG/DXF writeback.
/// Ensures target-language font styles exist and maps entity styles
/// from source language to target language (e.g. SimHei → Arial).
/// </summary>
internal static class DwgFontManager
{
    /// <summary>
    /// Pre-create target font styles in the document so entities can reference them.
    /// </summary>
    public static void EnsureFontStyles(CadDocument doc, bool cnToEn)
    {
        try
        {
            if (cnToEn)
            {
                EnsureStyle(doc, "Arial");
                EnsureStyle(doc, "Helvetica");
            }
            else
            {
                EnsureStyle(doc, "SimHei");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to ensure font styles");
        }
    }

    private static void EnsureStyle(CadDocument doc, string styleName)
    {
        if (doc.TextStyles.Contains(styleName)) return;

        var style = new TextStyle(styleName);

        // Set font file reference so the output DWG/DXF renders text correctly.
        // Without this, viewers may show empty rectangles instead of glyphs.
        bool isShx = styleName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
        if (isShx)
        {
            // IsShapeFile is automatically set by ACadSharp when Filename is assigned
            style.Filename = styleName;
        }
        // For TrueType fonts, the style name IS the font name — no additional
        // properties needed. DWG/DXF viewers resolve "Arial" → system Arial font.

        doc.TextStyles.Add(style);
    }

    /// <summary>
    /// Resolves a TextStyle by mapped font name: looks up an existing style
    /// or creates a new one. Returns null when the original style maps to nothing.
    /// </summary>
    public static TextStyle? ResolveTextStyle(string originalStyleName, bool cnToEn, CadDocument doc)
    {
        var targetFont = FontMapper.MapFontName(originalStyleName, cnToEn);
        if (string.IsNullOrEmpty(targetFont)) return null;

        foreach (var ts in doc.TextStyles)
        {
            if (string.Equals(ts.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                return ts;
        }

        var newStyle = new TextStyle(targetFont);

        // Set font file reference for SHX fonts (e.g. "romans.shx", "simplex.shx").
        // Without Filename, the DWG/DXF viewer can't locate the SHX file and may
        // show empty rectangles. TrueType fonts (e.g. "Arial", "SimHei") are
        // resolved by style name alone.
        bool isShx = targetFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
        if (isShx)
        {
            // IsShapeFile is automatically set by ACadSharp when Filename is assigned
            newStyle.Filename = targetFont;
        }

        doc.TextStyles.Add(newStyle);
        return newStyle;
    }

    /// <summary>
    /// Map Chinese fonts to English fonts and vice versa for single-line text.
    /// </summary>
    public static void ApplyFontMapping(CadText textEntity, string originalStyleName, bool cnToEn, CadDocument doc)
    {
        try
        {
            var style = ResolveTextStyle(originalStyleName, cnToEn, doc);
            if (style != null) textEntity.Style = style;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for {Style}", originalStyleName);
        }
    }

    /// <summary>
    /// Map Chinese fonts to English fonts and vice versa for multi-line text.
    /// </summary>
    public static void ApplyFontMapping(CadMText mtext, string originalStyleName, bool cnToEn, CadDocument doc)
    {
        try
        {
            var style = ResolveTextStyle(originalStyleName, cnToEn, doc);
            if (style != null) mtext.Style = style;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for {Style}", originalStyleName);
        }
    }
}
