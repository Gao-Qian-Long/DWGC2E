using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Header;
using ACadSharp.Tables;
using DwgTranslator.Core.Models;
using Serilog;
using System.Threading;
using CadEntity = ACadSharp.Entities.Entity;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttribute = ACadSharp.Entities.AttributeEntity;
using CadLwPolyline = ACadSharp.Entities.LwPolyline;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Writes translated text back into DWG and DXF files using ACadSharp library (offline, no AutoCAD required).
/// Includes: automatic backup, font mapping, and adaptive text scaling.
/// </summary>
public class DwgWriterService : IDwgWriterService, IDxfWriterService
{
    private const double MinFrameAreaSquareUnits = 10000;

    /// <inheritdoc/>
    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool cnToEn = true, CancellationToken cancellationToken = default)
    {
        var result = new CadWriteResult();

        if (!File.Exists(sourceFilePath))
        {
            result.Errors.Add($"Source file not found: {sourceFilePath}");
            Log.Error("Source CAD file not found: {Path}", sourceFilePath);
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

        // Detect file format
        var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var outputExtension = Path.GetExtension(outputFilePath).ToLowerInvariant();
        var isDxfSource = sourceExtension == ".dxf";
        var isDxfOutput = outputExtension == ".dxf";

        if (isDxfSource)
        {
            bool isBinary = DxfReader.IsBinary(sourceFilePath);
            Log.Information("Source DXF format: {Type}", isBinary ? "Binary" : "ASCII");
        }

        Log.Information("Writing translations to {Format}: {Source} -> {Output} ({Count} entities, CnToEn={Dir})",
            isDxfOutput ? "DXF" : "DWG", sourceFilePath, outputFilePath, entities.Count, cnToEn);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Read the original file using appropriate reader
            CadDocument doc;
            if (isDxfSource)
            {
                using var reader = new DxfReader(sourceFilePath, OnCadNotification);
                doc = reader.Read();
            }
            else
            {
                doc = DwgReader.Read(sourceFilePath);
            }
            if (doc == null)
            {
                result.Errors.Add($"Failed to read {(isDxfSource ? "DXF" : "DWG")} file");
                return result;
            }

            // Ensure output font styles exist in document
            EnsureFontStyles(doc, cnToEn);

            // Detect frame boundaries for collision-aware scaling
            var frames = DetectFrames(doc);
            if (frames.Count > 0)
                Log.Information("Detected {Count} frame boundary rectangles", frames.Count);

            // Build lookup: handle -> translated text
            var translationMap = new Dictionary<string, CoreTextEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (entity.Status == TranslationStatus.Translated ||
                    entity.Status == TranslationStatus.Reviewed)
                {
                    var handle = CleanHandle(entity.Handle);
                    if (!string.IsNullOrEmpty(handle))
                        translationMap.TryAdd(handle, entity);
                }
            }

            Log.Information("Translation map: {Count} entities to replace", translationMap.Count);

            // Process model space (directly - NOT via BlockRecords)
            int modelSpaceCount = ProcessEntityCollection(doc.ModelSpace.Entities, translationMap, result, cnToEn, doc, frames);
            Log.Information("ModelSpace: {Count} replacements", modelSpaceCount);

            // Process all layouts (paper space) directly
            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock == null) continue;

                int layoutCount = ProcessEntityCollection(
                    layout.AssociatedBlock.Entities, translationMap, result, cnToEn, doc, frames);
                if (layoutCount > 0)
                    Log.Debug("Layout '{Name}': {Count} replacements", layout.Name, layoutCount);
            }

            // Process user-defined block definitions only (skip internal Model/Paper space blocks)
            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord == null) continue;
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;

                int blockCount = ProcessEntityCollection(blockRecord.Entities, translationMap, result, cnToEn, doc, frames);
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

            cancellationToken.ThrowIfCancellationRequested();

            Log.Information("Writing {Format} output file...", isDxfOutput ? "DXF" : "DWG");
            if (isDxfOutput)
            {
                var dxfConfig = new DxfWriterConfiguration();
                DxfWriter.Write(outputFilePath, doc, false, dxfConfig, OnCadNotification);
            }
            else
            {
                using var writer = new DwgWriter(outputFilePath, doc);
                writer.Write();
            }

            Log.Information("{Format} writeback complete: {Success} replaced, {Failed} failed",
                isDxfOutput ? "DXF" : "DWG", result.SuccessCount, result.FailCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Format} writeback failed", isDxfOutput ? "DXF" : "DWG");
            result.Errors.Add($"Writeback error: {ex.Message}");
        }

        return result;
    }

    /// <inheritdoc/>
    CadWriteResult IDxfWriterService.WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool cnToEn, CancellationToken cancellationToken)
    {
        return WriteTranslations(sourceFilePath, outputFilePath, entities, cnToEn, cancellationToken);
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
        doc.TextStyles.Add(style);
    }

    /// <summary>
    /// Process a collection of entities, replacing text for matching handles.
    /// Uses pre-built translationMap (Handle→Entity) for O(1) lookup per entity.
    /// Returns count of successful replacements.
    /// </summary>
    private static int ProcessEntityCollection(
        IEnumerable<CadEntity> entities,
        Dictionary<string, CoreTextEntity> translationMap,
        CadWriteResult result,
        bool cnToEn,
        CadDocument doc,
        List<(double minX, double minY, double maxX, double maxY)> frames)
    {
        // Early exit: no more entities to replace in this collection
        if (translationMap.Count == 0) return 0;

        // Materialize to list so we can iterate over all entities for collision detection
        var entityList = entities as List<CadEntity> ?? [.. entities];

        int replacedCount = 0;

        foreach (var cadEntity in entityList)
        {
            if (cadEntity == null) continue;

            var handleStr = FormatHandle(cadEntity.Handle);

            if (translationMap.TryGetValue(handleStr, out var translatedEntity))
            {
                var success = ReplaceEntityText(cadEntity, translatedEntity, cnToEn, doc, frames);
                if (success)
                {
                    result.SuccessCount++;
                    translationMap.Remove(handleStr);
                    replacedCount++;

                    // Entity-level collision detection: after text replacement,
                    // check whether the new text overlaps nearby geometry and scale
                    // height down via binary search if it does.
                    double originalHeight = translatedEntity.OriginalHeight > 0 ? translatedEntity.OriginalHeight : 0;
                    if (originalHeight > 0)
                        ScaleDownToAvoidCollisions(cadEntity, originalHeight, entityList);

                    // Exit loop early if all translations have been applied
                    if (translationMap.Count == 0) break;
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
                if (translationMap.Count == 0) break;
            }
        }

        return replacedCount;
    }

    /// <summary>
    /// Replace text for attributes inside a block insert.
    /// </summary>
    private static void ProcessInsertAttributes(
        CadInsert insert,
        Dictionary<string, CoreTextEntity> translationMap,
        CadWriteResult result,
        ref int replacedCount)
    {
        foreach (var att in insert.Attributes)
        {
            if (att is not CadAttribute attEntity) continue;

            var compoundHandle = $"{FormatHandle(insert.Handle)}/{attEntity.Tag}";

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
    private static bool ReplaceEntityText(CadEntity entity, CoreTextEntity ourEntity, bool cnToEn, CadDocument doc,
        List<(double minX, double minY, double maxX, double maxY)> frames)
    {
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;

            double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : 0;

            switch (entity)
            {
                case CadText textEntity when entity is not CadMText:
                    textEntity.Value = translatedText;
                    ApplyFontMapping(textEntity, ourEntity.TextStyleName, cnToEn, doc);
                    ApplyScaling(textEntity, translatedText, ourEntity);
                    if (frames.Count > 0 && originalHeight > 0)
                        CheckAndScaleToFitFrame(textEntity, frames, originalHeight);
                    return true;

                case CadMText mtext:
                    mtext.Value = translatedText.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
                    ApplyFontMapping(mtext, ourEntity.TextStyleName, cnToEn, doc);
                    ApplyScaling(mtext, translatedText, ourEntity);
                    if (frames.Count > 0 && originalHeight > 0)
                        CheckAndScaleToFitFrame(mtext, frames, originalHeight);
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
    /// Resolves a TextStyle by mapped font name: looks up an existing style
    /// or creates a new one. Returns null when the original style maps to nothing.
    /// Shared by all ApplyFontMapping overloads to eliminate duplicated lookup logic.
    /// </summary>
    private static TextStyle? ResolveTextStyle(string originalStyleName, bool cnToEn, CadDocument doc)
    {
        var targetFont = FontMapper.MapFontName(originalStyleName, cnToEn);
        if (string.IsNullOrEmpty(targetFont)) return null;

        foreach (var ts in doc.TextStyles)
        {
            if (string.Equals(ts.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                return ts;
        }

        var newStyle = new TextStyle(targetFont);
        doc.TextStyles.Add(newStyle);
        return newStyle;
    }

    /// <summary>
    /// Map Chinese fonts to English fonts and vice versa.
    /// </summary>
    private static void ApplyFontMapping(CadText textEntity, string originalStyleName, bool cnToEn, CadDocument doc)
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

    private static void ApplyFontMapping(CadMText mtext, string originalStyleName, bool cnToEn, CadDocument doc)
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

    /// <summary>
    /// Auto-scale text height when translated text is significantly wider than original.
    /// Uses improved per-character width estimation (CJK vs ASCII).
    /// Also applies a conservative cap to reduce collision risk with nearby geometry.
    /// </summary>
    private static void ApplyScaling(CadText textEntity, string translatedText, CoreTextEntity ourEntity)
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
                if (scale < 0.5) scale = 0.5; // keep at least 50% of original height (lowered from 0.6 for collision avoidance)
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
    /// 2. FIXED-RECTANGLE MText: shrinks rectangle to match actual text width,
    ///    preventing stretched word spacing in justified/fit modes.
    /// 3. FREE-WIDTH MText: sets a tight rectangle width that respects per-line
    ///    widths (not concatenated) to avoid over-wide sparse layout.
    /// 4. Conservative height cap: never exceeds original total height to reduce
    ///    collision risk with nearby geometry.
    /// </summary>
    private static void ApplyScaling(CadMText mtext, string translatedText, CoreTextEntity ourEntity)
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

            // Split into logical lines (by \P) for per-line width estimation.
            var logicalLines = SplitMTextLines(translatedText);
            double maxLineWidth = 0;
            int lineCount = logicalLines.Count;
            foreach (var line in logicalLines)
            {
                double lineW = EstimateTextWidth(line, currentHeight);
                if (lineW > maxLineWidth) maxLineWidth = lineW;
            }

            // Also compute concatenated width for overflow detection
            string textForEstimation = translatedText.Replace("\\P", " ");
            double concatenatedWidth = EstimateTextWidth(textForEstimation, currentHeight);

            // Original line count for height budgeting
            var originalLogicalLines = SplitMTextLines(ourEntity.RawText ?? string.Empty);
            int originalLineCount = Math.Max(1, originalLogicalLines.Count);

            // Use per-line max width (not concatenated) for multi-line text
            double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;

            if (mtext.RectangleWidth > 0)
            {
                // FIXED rectangle width: the original was tuned for the source language.
                double originalRectWidth = mtext.RectangleWidth;
                double widthRatio = effectiveWidth / originalRectWidth;

                // RADICAL FIX: When translated text is significantly narrower than the
                // original fixed rectangle width, use free-width mode (Width=0).
                // This lets the viewer auto-size the rectangle to fit content,
                // which is the only reliable way to prevent word-stretching in MText.
                // ACadSharp may not reliably serialize RectangleWidth changes, but
                // setting it to 0 (free width) is a well-supported operation.
                if (widthRatio < 0.75)
                {
                    // Text much narrower — use free-width for natural tight spacing
                    mtext.RectangleWidth = 0;
                    Log.Debug("MText {Handle}: set free-width (was {Orig:F1}, text={Eff:F1}, ratio={R:F2})",
                        mtext.Handle, originalRectWidth, effectiveWidth, widthRatio);
                }
                else if (widthRatio < 0.95)
                {
                    // Slightly narrower — reduce width proportionally
                    double targetWidth = Math.Max(effectiveWidth * 1.08, originalRectWidth * 0.50);
                    mtext.RectangleWidth = targetWidth;
                    Log.Debug("MText {Handle}: reduced width {Orig:F1} -> {New:F1} (ratio={R:F2})",
                        mtext.Handle, originalRectWidth, targetWidth, widthRatio);
                }
                // else: text fills the rectangle well — keep original width

                // Estimate line count in the effective rectangle width
                double rectWidth = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : effectiveWidth * 1.1;
                int estimatedLines = 0;
                foreach (var line in logicalLines)
                {
                    double lineW = EstimateTextWidth(line, currentHeight);
                    estimatedLines += Math.Max(1, (int)Math.Ceiling(lineW / rectWidth));
                }
                estimatedLines = Math.Max(1, estimatedLines);

                if (estimatedLines > originalLineCount)
                {
                    double originalTotalHeight = originalLineCount * originalHeight * originalLineSpacing;
                    double translatedTotalHeight = estimatedLines * currentHeight * originalLineSpacing;

                    if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
                    {
                        double scale = originalTotalHeight / translatedTotalHeight;
                        double newHeight = currentHeight * scale;
                        double minHeight = originalHeight * 0.4;
                        if (newHeight < minHeight) newHeight = minHeight;
                        if (newHeight < currentHeight)
                        {
                            mtext.Height = newHeight;
                            currentHeight = newHeight;
                        }
                    }
                }
            }
            else
            {
                // Original was FREE width: only set a rectangle width when truly necessary
                // to prevent extremely long single lines from spanning the entire drawing.
                if (effectiveWidth > originalWidth * 1.3)
                {
                    double maxAllowable = originalWidth * 1.3;
                    double targetWidth = Math.Min(maxAllowable, Math.Max(originalWidth * 1.05, effectiveWidth * 0.65));
                    mtext.RectangleWidth = targetWidth;
                }
                // else: keep free width (RectangleWidth = 0) for natural tight spacing
            }

            // Conservative collision-avoidance cap: never let height grow beyond
            // original height + 5%. This reduces risk of overlapping nearby geometry
            // in the offline path where we cannot do real collision detection.
            if (mtext.Height > originalHeight * 1.05)
            {
                mtext.Height = originalHeight * 1.05;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MText scaling failed for entity {Handle}", mtext.Handle);
        }
    }

    private static readonly string[] MTextLineSeparator = ["\\P"];

    /// <summary>
    /// Splits MText content into logical lines by \P (hard paragraph break).
    /// Empty lines are preserved as empty strings.
    /// </summary>
    private static List<string> SplitMTextLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        return [.. text.Split(MTextLineSeparator, StringSplitOptions.None)];
    }

    private static double EstimateTextWidth(string text, double height) => TextWidthEstimator.EstimateTextWidth(text, height);

    private static int EstimateLineCount(string text, double height, double rectWidth) => TextWidthEstimator.EstimateLineCount(text, height, rectWidth);

    // ───────────────────────── Frame detection and boundary checking ─────────────────────────

    /// <summary>
    /// Detects rectangular frame boundaries in model space by scanning for closed LwPolylines
    /// with exactly 4 vertices and a large area. These frames are used as soft boundaries to
    /// prevent translated text from overflowing.
    /// </summary>
    private static List<(double minX, double minY, double maxX, double maxY)> DetectFrames(CadDocument doc)
    {
        var frames = new List<(double, double, double, double)>();
        try
        {
            foreach (var entity in doc.ModelSpace.Entities)
            {
                if (entity is CadLwPolyline poly && poly.IsClosed)
                {
                    if (poly.Vertices.Count == 4)
                    {
                        double minX = poly.Vertices.Min(v => v.Location.X);
                        double minY = poly.Vertices.Min(v => v.Location.Y);
                        double maxX = poly.Vertices.Max(v => v.Location.X);
                        double maxY = poly.Vertices.Max(v => v.Location.Y);
                        double area = (maxX - minX) * (maxY - minY);
                        // Only consider large rectangles as frames (not small detail boxes)
                        if (area > MinFrameAreaSquareUnits)
                            frames.Add((minX, minY, maxX, maxY));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame detection encountered an error (non-fatal)");
        }
        return frames;
    }

    /// <summary>
    /// Finds the frame whose center is closest to the given point.
    /// Returns null if no frames are available.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY)? FindClosestFrame(
        double px, double py,
        List<(double minX, double minY, double maxX, double maxY)> frames)
    {
        if (frames.Count == 0) return null;
        return frames.OrderBy(f =>
            Math.Pow((f.minX + f.maxX) / 2 - px, 2) +
            Math.Pow((f.minY + f.maxY) / 2 - py, 2)).First();
    }

    /// <summary>
    /// Estimates the bounding box of a CadText entity based on its insertion point,
    /// text width, and height. The insertion point is at the left baseline.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY) EstimateTextBounds(CadText textEntity)
    {
        double w = EstimateTextWidth(textEntity.Value ?? string.Empty, textEntity.Height);
        double h = textEntity.Height;
        double x = textEntity.InsertPoint.X;
        double y = textEntity.InsertPoint.Y;
        // Text extends right from insert point, and roughly from y to y+height
        return (x, y, x + w, y + h);
    }

    /// <summary>
    /// Estimates the bounding box of a CadMText entity based on its insertion point,
    /// rectangle width (or estimated width for free-width text), and estimated height.
    /// The insertion point is the top-left corner for default (top-left) attachment.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY) EstimateMTextBounds(CadMText mtext)
    {
        double w;
        if (mtext.RectangleWidth > 0)
        {
            w = mtext.RectangleWidth;
        }
        else
        {
            // Free-width: estimate from content
            string plain = mtext.Value?.Replace("\\P", " ") ?? "";
            w = EstimateTextWidth(plain, mtext.Height);
        }

        // Estimate total height from line count
        int lineCount = EstimateLineCount(mtext.Value ?? "", mtext.Height, Math.Max(w, 1.0));
        double lineSpacing = mtext.LineSpacing > 0 ? mtext.LineSpacing : 1.0;
        double totalHeight = lineCount * mtext.Height * lineSpacing;

        double x = mtext.InsertPoint.X;
        double y = mtext.InsertPoint.Y;
        // Default attachment is top-left: text extends right and downward
        return (x, y - totalHeight, x + w, y);
    }

    /// <summary>
    /// Computes the scale factor required to fit text bounds within a frame boundary.
    /// Returns a value in [0.4, 1.0]. Shared by all CheckAndScaleToFitFrame overloads.
    /// </summary>
    private static double ComputeFrameScale(double frameW, double frameH, double textW, double textH)
    {
        double scaleW = textW > 0 ? Math.Max(0.4, (frameW * 0.95) / textW) : 1.0;
        double scaleH = textH > 0 ? Math.Max(0.4, (frameH * 0.95) / textH) : 1.0;
        return Math.Min(1.0, Math.Min(scaleW, scaleH));
    }

    /// <summary>
    /// Checks whether a CadText entity's estimated bounds exceed its closest frame boundary.
    /// If so, scales down the text height to fit within the frame (down to 40% of original height).
    /// </summary>
    private static void CheckAndScaleToFitFrame(
        CadText textEntity,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        double originalHeight)
    {
        try
        {
            double px = textEntity.InsertPoint.X;
            double py = textEntity.InsertPoint.Y;
            var frame = FindClosestFrame(px, py, frames);
            if (!frame.HasValue) return;

            var bounds = EstimateTextBounds(textEntity);
            if (!ExceedsFrame(bounds, frame.Value)) return;

            double frameW = frame.Value.maxX - frame.Value.minX;
            double frameH = frame.Value.maxY - frame.Value.minY;
            double textW = bounds.maxX - bounds.minX;
            double textH = bounds.maxY - bounds.minY;

            double scale = ComputeFrameScale(frameW, frameH, textW, textH);

            if (scale < 1.0)
            {
                double newHeight = textEntity.Height * scale;
                double minHeight = originalHeight * 0.4;
                if (newHeight < minHeight) newHeight = minHeight;
                textEntity.Height = newHeight;
                Log.Debug("Frame-boundary scaling: Text {Handle} scaled by {Scale:F2} (height {Old:F2} -> {New:F2})",
                    textEntity.Handle, scale, originalHeight, newHeight);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame boundary check failed for text {Handle} (non-fatal)", textEntity.Handle);
        }
    }

    /// <summary>
    /// Checks whether an MText entity's estimated bounds exceed its closest frame boundary.
    /// If so, scales down the text height to fit within the frame (down to 40% of original height).
    /// </summary>
    private static void CheckAndScaleToFitFrame(
        CadMText mtext,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        double originalHeight)
    {
        try
        {
            double px = mtext.InsertPoint.X;
            double py = mtext.InsertPoint.Y;
            var frame = FindClosestFrame(px, py, frames);
            if (!frame.HasValue) return;

            var bounds = EstimateMTextBounds(mtext);
            if (!ExceedsFrame(bounds, frame.Value)) return;

            double frameW = frame.Value.maxX - frame.Value.minX;
            double frameH = frame.Value.maxY - frame.Value.minY;
            double textW = bounds.maxX - bounds.minX;
            double textH = bounds.maxY - bounds.minY;

            double scale = ComputeFrameScale(frameW, frameH, textW, textH);

            if (scale < 1.0)
            {
                double newHeight = mtext.Height * scale;
                double minHeight = originalHeight * 0.4;
                if (newHeight < minHeight) newHeight = minHeight;
                mtext.Height = newHeight;
                Log.Debug("Frame-boundary scaling: MText {Handle} scaled by {Scale:F2} (height {Old:F2} -> {New:F2})",
                    mtext.Handle, scale, originalHeight, newHeight);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame boundary check failed for MText {Handle} (non-fatal)", mtext.Handle);
        }
    }

    /// <summary>
    /// Returns true if the text bounding box exceeds the frame boundaries.
    /// Uses a small tolerance (1% of frame dimensions) to avoid false positives.
    /// </summary>
    private static bool ExceedsFrame(
        (double minX, double minY, double maxX, double maxY) bounds,
        (double minX, double minY, double maxX, double maxY) frame)
    {
        double tolX = (frame.maxX - frame.minX) * 0.01;
        double tolY = (frame.maxY - frame.minY) * 0.01;
        return bounds.minX < frame.minX - tolX
            || bounds.maxX > frame.maxX + tolX
            || bounds.minY < frame.minY - tolY
            || bounds.maxY > frame.maxY + tolY;
    }

    // ───────────────────── Entity-level collision detection ─────────────────────

    /// <summary>
    /// Estimates the axis-aligned bounding box of any CAD entity for collision detection.
    /// Returns null for entity types whose bounds cannot be reliably estimated.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY)? GetEntityBounds(CadEntity entity)
    {
        switch (entity)
        {
            case CadText text:
                return EstimateTextBounds(text);
            case CadMText mtext:
                return EstimateMTextBounds(mtext);
            case CadInsert insert:
                double x = insert.InsertPoint.X;
                double y = insert.InsertPoint.Y;
                double w = Math.Abs(insert.XScale) * 80;
                double h = Math.Abs(insert.YScale) * 80;
                return (x, y, x + w, y + h);
            case CadLwPolyline poly:
                if (poly.Vertices.Count > 0)
                {
                    double pminX = poly.Vertices.Min(v => v.Location.X);
                    double pminY = poly.Vertices.Min(v => v.Location.Y);
                    double pmaxX = poly.Vertices.Max(v => v.Location.X);
                    double pmaxY = poly.Vertices.Max(v => v.Location.Y);
                    return (pminX, pminY, pmaxX, pmaxY);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Returns true if two axis-aligned bounding boxes overlap, with a configurable margin.
    /// </summary>
    private static bool HasBoundsOverlap(
        (double minX, double minY, double maxX, double maxY) a,
        (double minX, double minY, double maxX, double maxY) b,
        double margin = 2.0)
    {
        return a.minX - margin < b.maxX &&
               a.maxX + margin > b.minX &&
               a.minY - margin < b.maxY &&
               a.maxY + margin > b.minY;
    }

    /// <summary>
    /// Sets the Height property on text entities (CadText or CadMText).
    /// No-op for other entity types.
    /// </summary>
    private static void TrySetEntityHeight(CadEntity entity, double height)
    {
        switch (entity)
        {
            case CadText text:
                text.Height = height;
                break;
            case CadMText mtext:
                mtext.Height = height;
                break;
        }
    }

    /// <summary>
    /// Binary-search scales down text height to avoid overlapping nearby entities.
    /// Called after a successful text replacement in ProcessEntityCollection.
    /// Collects bounds of all OTHER entities in the same collection, checks for
    /// overlaps with the target entity, and scales height down (≥ 40% original)
    /// to find the largest non-overlapping size.
    /// </summary>
    private static void ScaleDownToAvoidCollisions(
        CadEntity targetEntity,
        double originalHeight,
        IEnumerable<CadEntity> allEntities)
    {
        if (originalHeight <= 0) return;

        double currentHeight;
        switch (targetEntity)
        {
            case CadText text:
                currentHeight = text.Height;
                break;
            case CadMText mtext:
                currentHeight = mtext.Height;
                break;
            default:
                return;
        }

        double minHeight = originalHeight * 0.4;
        if (currentHeight <= minHeight) return;

        // Collect bounds of all OTHER entities in the collection
        var otherBounds = new List<(double minX, double minY, double maxX, double maxY)>();
        foreach (var other in allEntities)
        {
            if (ReferenceEquals(other, targetEntity) || other == null) continue;
            var b = GetEntityBounds(other);
            if (b.HasValue) otherBounds.Add(b.Value);
        }

        if (otherBounds.Count == 0) return;

        // Check current bounds for any overlap
        var currentBounds = GetEntityBounds(targetEntity);
        if (!currentBounds.HasValue) return;

        bool hasCollision = false;
        foreach (var ob in otherBounds)
        {
            if (HasBoundsOverlap(currentBounds.Value, ob))
            {
                hasCollision = true;
                break;
            }
        }

        if (!hasCollision) return;

        // Binary search for maximum non-overlapping height
        double lo = minHeight;
        double hi = currentHeight;
        double savedHeight = currentHeight;

        for (int iter = 0; iter < 15; iter++)
        {
            double mid = (lo + hi) / 2;
            TrySetEntityHeight(targetEntity, mid);

            var testBounds = GetEntityBounds(targetEntity);
            if (!testBounds.HasValue) break;

            bool midHasCollision = false;
            foreach (var ob in otherBounds)
            {
                if (HasBoundsOverlap(testBounds.Value, ob))
                {
                    midHasCollision = true;
                    break;
                }
            }

            if (midHasCollision)
                hi = mid;
            else
                lo = mid;
        }

        TrySetEntityHeight(targetEntity, lo);

        if (lo < savedHeight * 0.99)
        {
            Log.Debug("Collision-avoidance scaling: {Type} {Handle} height {Old:F2} -> {New:F2}",
                targetEntity.GetType().Name, targetEntity.Handle, savedHeight, lo);
        }
    }

    // ─────────────────────────── Handle formatting ───────────────────────────

    /// <summary>
    /// Formats a numeric (ulong) handle to its canonical string representation (uppercase hexadecimal).
    /// This MUST match how handles are stored in TextEntity.Handle by DwgReaderService,
    /// otherwise lookup will silently fail and no text will be replaced.
    /// </summary>
    private static string FormatHandle(ulong handle) => handle.ToString("X");

    /// <summary>
    /// Clean a handle string for comparison.
    /// </summary>
    private static string CleanHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        // Preserve compound handles (attributes: "handle/tag", table cells: "handle:row:col")
        if (handle.Contains('/') || handle.Contains(':')) return handle;
        // Remove any non-hex characters and normalize to uppercase
        return handle.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Handles ACadSharp notification events during CAD file operations.
    /// Captures warnings and errors from the DXF/DWG writer for structured logging.
    /// </summary>
    private static void OnCadNotification(object sender, ACadSharp.IO.NotificationEventArgs e)
    {
        if (e.Exception != null)
            Log.Warning(e.Exception, "ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
        else
            Log.Debug("ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
    }
}
