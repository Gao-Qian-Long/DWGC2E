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
    public static void EnsureFontStyles(CadDocument doc, bool targetIsCjk)
    {
        try
        {
            EnsureMappedStyle(doc, targetIsCjk ? "SimHei" : "Arial");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to ensure font styles");
        }
    }

    /// <summary>
    /// Resolves a TextStyle by mapped font name: looks up an existing style
    /// or creates a new one. Returns null when the original style maps to nothing.
    /// </summary>
    public static TextStyle? ResolveTextStyle(string originalStyleName, bool targetIsCjk, CadDocument doc)
    {
        var original = doc.TextStyles.FirstOrDefault(ts =>
            string.Equals(ts.Name, originalStyleName, StringComparison.OrdinalIgnoreCase));
        var targetFont = FontMapper.MapFontName(originalStyleName,
            original?.Filename, original?.BigFontFilename, targetIsCjk);
        if (string.IsNullOrEmpty(targetFont)) return null;
        return EnsureMappedStyle(doc, targetFont);
    }

    private static TextStyle EnsureMappedStyle(CadDocument doc, string targetFont)
    {
        // Do not reuse a user style named "Arial"/"SimHei": that name may point to a
        // different SHX or have a fixed height/width. The private style carries an explicit
        // font file and neutral metrics so the output renderer cannot silently fall back to
        // the generic txt.shx face (the source of the thin/odd-looking offline glyphs).
        var styleName = "DWGC2E_" + targetFont.Replace('.', '_');
        var style = doc.TextStyles.FirstOrDefault(ts =>
            string.Equals(ts.Name, styleName, StringComparison.OrdinalIgnoreCase));
        if (style == null)
        {
            style = new TextStyle(styleName);
            doc.TextStyles.Add(style);
        }

        style.Filename = FontMapper.FontFileName(targetFont);
        style.BigFontFilename = string.Empty;
        style.Height = 0;
        style.Width = 1;
        style.ObliqueAngle = 0;
        style.TrueType = FontFlags.Regular;
        return style;
    }

    /// <summary>
    /// Map Chinese fonts to English fonts and vice versa for single-line text.
    /// </summary>
    public static void ApplyFontMapping(CadText textEntity, string originalStyleName, bool targetIsCjk, CadDocument doc)
    {
        try
        {
            var style = ResolveTextStyle(originalStyleName, targetIsCjk, doc);
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
    public static void ApplyFontMapping(CadMText mtext, string originalStyleName, bool targetIsCjk, CadDocument doc)
    {
        try
        {
            var style = ResolveTextStyle(originalStyleName, targetIsCjk, doc);
            if (style != null) mtext.Style = style;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for {Style}", originalStyleName);
        }
    }
}
