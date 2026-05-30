using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Header;
using ACadSharp.Tables;
using DwgTranslator.Core.Models;
using Serilog;
using System.Text.RegularExpressions;
using CadEntity = ACadSharp.Entities.Entity;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttribute = ACadSharp.Entities.AttributeEntity;
using OurTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Writes translated text back into DWG files using ACadSharp library (offline, no AutoCAD required).
/// Includes: automatic backup, font mapping, and adaptive text scaling.
/// </summary>
public class DwgWriterService : IDwgWriterService
{
    private static readonly Regex HandleRegex = new(@"^[0-9A-Fa-f]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public DwgWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<OurTextEntity> entities, bool cnToEn = true)
    {
        var result = new DwgWriteResult();

        if (!File.Exists(sourceFilePath))
        {
            result.Errors.Add($"Source file not found: {sourceFilePath}");
            Log.Error("Source DWG file not found: {Path}", sourceFilePath);
            return result;
        }

        if (entities.Count == 0)
        {
            result.Errors.Add("No entities to write");
            Log.Warning("No entities provided for writeback");
            return result;
        }

        // 1. Auto-backup original file
        try
        {
            var backupPath = sourceFilePath + ".bak";
            File.Copy(sourceFilePath, backupPath, overwrite: true);
            Log.Information("Backup created: {Backup}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create backup for {Path}", sourceFilePath);
        }

        Log.Information("Writing translations to DWG: {Source} -> {Output} ({Count} entities, CnToEn={Dir})",
            sourceFilePath, outputFilePath, entities.Count, cnToEn);

        try
        {
            // Read the original DWG
            var doc = DwgReader.Read(sourceFilePath);
            if (doc == null)
            {
                result.Errors.Add("Failed to read DWG file");
                return result;
            }

            // Ensure output font styles exist in document
            EnsureFontStyles(doc, cnToEn);

            // Build lookup: handle -> translated text
            var translationMap = new Dictionary<string, OurTextEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (entity.Status == TranslationStatus.Translated ||
                    entity.Status == TranslationStatus.Reviewed)
                {
                    var handle = CleanHandle(entity.Handle);
                    if (!string.IsNullOrEmpty(handle) && !translationMap.ContainsKey(handle))
                        translationMap[handle] = entity;
                }
            }

            Log.Information("Translation map: {Count} entities to replace", translationMap.Count);

            // Process model space (directly - NOT via BlockRecords)
            int modelSpaceCount = ProcessEntityCollection(doc.ModelSpace.Entities, translationMap, result, cnToEn, doc);
            Log.Information("ModelSpace: {Count} replacements", modelSpaceCount);

            // Process all layouts (paper space) directly
            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock == null) continue;

                int layoutCount = ProcessEntityCollection(
                    layout.AssociatedBlock.Entities, translationMap, result, cnToEn, doc);
                if (layoutCount > 0)
                    Log.Debug("Layout '{Name}': {Count} replacements", layout.Name, layoutCount);
            }

            // Process user-defined block definitions only (skip internal Model/Paper space blocks)
            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord == null) continue;
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;

                int blockCount = ProcessEntityCollection(blockRecord.Entities, translationMap, result, cnToEn, doc);
                if (blockCount > 0)
                    Log.Debug("Block '{Name}': {Count} replacements", blockRecord.Name, blockCount);
            }

            int unprocessedCount = translationMap.Count;
            if (unprocessedCount > 0)
            {
                Log.Warning("Skipped {Count} entities: handles not found in DWG", unprocessedCount);
                foreach (var kv in translationMap)
                    Log.Debug("  Unmatched handle: {Handle}", kv.Key);
            }

            // Save the modified document
            var outputDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            Log.Information("Writing DWG output file...");
            using (var writer = new DwgWriter(outputFilePath, doc))
            {
                writer.Write();
            }

            Log.Information("DWG writeback complete: {Success} replaced, {Failed} failed",
                result.SuccessCount, result.FailCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DWG writeback failed");
            result.Errors.Add($"Writeback error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Pre-create target font styles in the document so entities can reference them.
    /// </summary>
    private static void EnsureFontStyles(CadDocument doc, bool cnToEn)
    {
        try
        {
            if (cnToEn)
            {
                EnsureStyle(doc, "Arial", null);
                EnsureStyle(doc, "Helvetica", null);
            }
            else
            {
                EnsureStyle(doc, "SimHei", "gbcbig.shx");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to ensure font styles");
        }
    }

    private static void EnsureStyle(CadDocument doc, string styleName, string? bigFontName)
    {
        if (doc.TextStyles.Contains(styleName)) return;

        var style = new TextStyle(styleName);
        doc.TextStyles.Add(style);
    }

    /// <summary>
    /// Process a collection of entities, replacing text for matching handles.
    /// Returns count of successful replacements.
    /// </summary>
    private static int ProcessEntityCollection(
        IEnumerable<CadEntity> entities,
        Dictionary<string, OurTextEntity> translationMap,
        DwgWriteResult result,
        bool cnToEn,
        CadDocument doc)
    {
        int replacedCount = 0;

        foreach (var cadEntity in entities)
        {
            if (cadEntity == null) continue;

            var handleStr = cadEntity.Handle.ToString();

            if (translationMap.TryGetValue(handleStr, out var translatedEntity))
            {
                var success = ReplaceEntityText(cadEntity, translatedEntity, cnToEn, doc);
                if (success)
                {
                    result.SuccessCount++;
                    translationMap.Remove(handleStr);
                    replacedCount++;
                }
                else
                {
                    result.FailCount++;
                    result.Errors.Add($"Failed to replace text for entity handle {handleStr}");
                }
            }

            // Handle nested inserts with attributes
            if (cadEntity is CadInsert insert)
            {
                ProcessInsertAttributes(insert, translationMap, result, ref replacedCount);
            }
        }

        return replacedCount;
    }

    /// <summary>
    /// Replace text for attributes inside a block insert.
    /// </summary>
    private static void ProcessInsertAttributes(
        CadInsert insert,
        Dictionary<string, OurTextEntity> translationMap,
        DwgWriteResult result,
        ref int replacedCount)
    {
        foreach (var att in insert.Attributes)
        {
            if (att is not CadAttribute attEntity) continue;

            var compoundHandle = $"{insert.Handle}/{attEntity.Tag}";

            if (translationMap.TryGetValue(compoundHandle, out var translatedEntity))
            {
                attEntity.Value = translatedEntity.TranslatedText;
                result.SuccessCount++;
                translationMap.Remove(compoundHandle);
                replacedCount++;
            }
        }
    }

    /// <summary>
    /// Replace text content of a CAD entity with translated text, applying font mapping and scaling.
    /// </summary>
    private static bool ReplaceEntityText(CadEntity entity, OurTextEntity ourEntity, bool cnToEn, CadDocument doc)
    {
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;

            switch (entity)
            {
                case CadText textEntity when entity is not CadMText:
                    textEntity.Value = translatedText;
                    ApplyFontMapping(textEntity, ourEntity.TextStyleName, cnToEn, doc);
                    ApplyScaling(textEntity, translatedText, ourEntity);
                    return true;

                case CadMText mtext:
                    mtext.Value = translatedText.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
                    ApplyFontMapping(mtext, ourEntity.TextStyleName, cnToEn, doc);
                    ApplyScaling(mtext, translatedText, ourEntity);
                    return true;

                case CadDimension dim:
                    dim.Text = translatedText;
                    return true;

                case CadMultiLeader mleader:
                    var ctx = mleader.ContextData;
                    if (ctx != null && ctx.HasTextContents)
                    {
                        ctx.TextLabel = translatedText;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to replace text for entity {Handle} type {Type}",
                entity.Handle, entity.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Map Chinese fonts to English fonts and vice versa.
    /// </summary>
    private static void ApplyFontMapping(CadText textEntity, string originalStyleName, bool cnToEn, CadDocument doc)
    {
        try
        {
            var targetFont = MapFontName(originalStyleName, cnToEn);
            if (string.IsNullOrEmpty(targetFont)) return;

            // Find or create target style safely (do NOT modify existing style.Name)
            TextStyle? targetStyle = null;
            foreach (var ts in doc.TextStyles)
            {
                if (string.Equals(ts.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                {
                    targetStyle = ts;
                    break;
                }
            }
            if (targetStyle == null)
            {
                targetStyle = new TextStyle(targetFont);
                doc.TextStyles.Add(targetStyle);
            }

            textEntity.Style = targetStyle;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for {Style}", originalStyleName);
        }
    }

    private static void ApplyFontMapping(CadMText mtext, string originalStyleName, bool cnToEn, CadDocument doc)
    {
        try
        {
            var targetFont = MapFontName(originalStyleName, cnToEn);
            if (string.IsNullOrEmpty(targetFont)) return;

            TextStyle? targetStyle = null;
            foreach (var ts in doc.TextStyles)
            {
                if (string.Equals(ts.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                {
                    targetStyle = ts;
                    break;
                }
            }
            if (targetStyle == null)
            {
                targetStyle = new TextStyle(targetFont);
                doc.TextStyles.Add(targetStyle);
            }

            mtext.Style = targetStyle;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for {Style}", originalStyleName);
        }
    }

    private static string? MapFontName(string currentStyleName, bool cnToEn)
    {
        if (cnToEn)
        {
            if (currentStyleName.Contains("SimHei", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("SimSun", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("宋体", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("黑体", StringComparison.OrdinalIgnoreCase))
                return "Arial";
        }
        else
        {
            if (currentStyleName.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("Times", StringComparison.OrdinalIgnoreCase))
                return "SimHei";
        }
        return null;
    }

    /// <summary>
    /// Auto-scale text height when translated text is significantly wider than original.
    /// Uses improved per-character width estimation (CJK vs ASCII).
    /// Also applies a conservative cap to reduce collision risk with nearby geometry.
    /// </summary>
    private static void ApplyScaling(CadText textEntity, string translatedText, OurTextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : textEntity.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        try
        {
            double newWidth = EstimateTextWidth(translatedText, textEntity.Height);
            if (newWidth > originalWidth)
            {
                double scale = originalWidth / newWidth;
                if (scale < 0.6) scale = 0.6; // keep at least 60% of original height
                double newHeight = originalHeight * scale;
                double oldHeight = textEntity.Height;
                textEntity.Height = newHeight;
                Log.Debug("Scaled text {Handle}: {OldH:F2} -> {NewH:F2}", textEntity.Handle, oldHeight, newHeight);
            }

            // Conservative safety cap: never allow height to grow beyond original
            // (translation usually makes text longer, not taller)
            if (textEntity.Height > originalHeight * 1.05)
            {
                textEntity.Height = originalHeight * 1.05;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scaling failed for text entity");
        }
    }

    /// <summary>
    /// Adaptive layout for MText:
    /// 1. Preserves original line spacing.
    /// 2. FIXED-RECTANGLE MText: never expands width (designer already set it).
    ///    Only scales down height when translated text needs more lines.
    /// 3. FREE-WIDTH MText: sets a conservative rectangle width based on
    ///    under-estimated text width to avoid over-wide sparse layout.
    /// </summary>
    private static void ApplyScaling(CadMText mtext, string translatedText, OurTextEntity ourEntity)
    {
        if (string.IsNullOrEmpty(translatedText) || ourEntity.OriginalWidth <= 0) return;

        try
        {
            double originalWidth = ourEntity.OriginalWidth;
            double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.Height;
            double currentHeight = mtext.Height;
            double originalLineSpacing = ourEntity.MTextLineSpacing > 0 ? ourEntity.MTextLineSpacing : 1.0;

            // Preserve original line spacing from source drawing
            if (ourEntity.MTextLineSpacing > 0)
                mtext.LineSpacing = ourEntity.MTextLineSpacing;
            if (ourEntity.MTextLineSpacingStyle > 0)
                mtext.LineSpacingStyle = (LineSpacingStyleType)ourEntity.MTextLineSpacingStyle;

            string textForEstimation = translatedText.Replace("\\P", " ");

            if (mtext.RectangleWidth > 0)
            {
                // FIXED rectangle width: do NOT expand — designer already tuned it.
                // Only scale down height if more lines are needed.
                double rectWidth = mtext.RectangleWidth;

                string originalText = (ourEntity.RawText ?? string.Empty).Replace("\\P", " ");
                int originalLines = EstimateLineCount(originalText, originalHeight, rectWidth);
                int translatedLines = EstimateLineCount(textForEstimation, currentHeight, rectWidth);

                if (translatedLines > originalLines)
                {
                    double originalTotalHeight = originalLines * originalHeight * originalLineSpacing;
                    double translatedTotalHeight = translatedLines * currentHeight * originalLineSpacing;

                    if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
                    {
                        double scale = originalTotalHeight / translatedTotalHeight;
                        double newHeight = currentHeight * scale;
                        double minHeight = originalHeight * 0.5;
                        if (newHeight < minHeight) newHeight = minHeight;

                        if (newHeight < currentHeight)
                        {
                            mtext.Height = newHeight;
                            currentHeight = newHeight;
                            translatedLines = EstimateLineCount(textForEstimation, currentHeight, rectWidth);
                            translatedTotalHeight = translatedLines * currentHeight * originalLineSpacing;
                        }
                    }

                    // Fine-tune line spacing only if still slightly overflowing
                    if (translatedTotalHeight > originalTotalHeight * 1.05 && originalTotalHeight > 0)
                    {
                        double spacing = originalTotalHeight / (translatedLines * currentHeight);
                        if (spacing < 0.6) spacing = 0.6;
                        if (spacing > originalLineSpacing) spacing = originalLineSpacing;
                        mtext.LineSpacing = spacing;
                    }
                }
            }
            else
            {
                // FREE width: NEVER create an over-wide rectangle.
                // Excessive RectangleWidth causes AutoCAD to stretch word spacing
                // (especially in justified/Fit modes), destroying readability.
                double translatedWidth = EstimateTextWidth(textForEstimation, currentHeight);
                if (translatedWidth > originalWidth * 1.3)
                {
                    // Significantly longer: cap width expansion to prevent sparse layout.
                    double maxAllowable = originalWidth * 1.3;
                    double preferred = translatedWidth * 0.65; // underestimate for compact lines
                    double targetWidth = Math.Min(maxAllowable, Math.Max(originalWidth * 1.05, preferred));
                    mtext.RectangleWidth = targetWidth;

                    double scale = originalWidth / translatedWidth;
                    if (scale < 0.6) scale = 0.6;
                    double newHeight = originalHeight * scale;
                    if (newHeight < currentHeight)
                        mtext.Height = newHeight;
                }
                else if (translatedWidth > originalWidth * 1.05)
                {
                    // Moderate growth: snug fit only
                    double targetWidth = Math.Min(translatedWidth * 1.05, originalWidth * 1.25);
                    mtext.RectangleWidth = targetWidth;
                }
                // else: keep RectangleWidth = 0 (true free-width) for natural tight spacing
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MText scaling failed for entity {Handle}", mtext.Handle);
        }
    }

    private static double EstimateTextWidth(string text, double height)
    {
        if (string.IsNullOrEmpty(text) || height <= 0) return 0;
        double width = 0;
        foreach (char c in text)
        {
            if (c == ' ')
                width += height * 0.20;
            else if (c >= 0x4E00 && c <= 0x9FFF)      // CJK Unified Ideographs
                width += height * 1.0;
            else if (c >= 0x3000 && c <= 0x303F)      // CJK Symbols and Punctuation
                width += height * 1.0;
            else if (c >= 0xFF00 && c <= 0xFFEF)      // Fullwidth forms
                width += height * 1.0;
            else if (c >= 0x3040 && c <= 0x309F)      // Hiragana
                width += height * 1.0;
            else if (c >= 0x30A0 && c <= 0x30FF)      // Katakana
                width += height * 1.0;
            else if (char.IsUpper(c))
                width += height * 0.55;
            else if (char.IsLower(c))
                width += height * 0.45;
            else if (char.IsDigit(c))
                width += height * 0.50;
            else
                width += height * 0.45;                // punctuation, symbols
        }
        return width;
    }

    private static int EstimateLineCount(string text, double height, double rectWidth)
    {
        if (rectWidth <= 0 || string.IsNullOrEmpty(text) || height <= 0) return 1;
        var hardLines = text.Split(new[] { "\\P", "\n", "\r\n" }, StringSplitOptions.None);
        int totalLines = 0;
        foreach (var line in hardLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                totalLines++;
                continue;
            }
            double lineWidth = EstimateTextWidth(line, height);
            totalLines += Math.Max(1, (int)Math.Ceiling(lineWidth / rectWidth));
        }
        return Math.Max(1, totalLines);
    }

    /// <summary>
    /// Clean a handle string for comparison.
    /// </summary>
    private static string CleanHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        if (handle.Contains('/') || handle.Contains(':')) return handle;
        return handle;
    }
}

/// <summary>
/// Result of a DWG writeback operation.
/// </summary>
public class DwgWriteResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsSuccess => SuccessCount > 0;
}
