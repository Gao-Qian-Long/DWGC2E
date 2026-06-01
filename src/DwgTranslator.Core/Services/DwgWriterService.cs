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

                // KEY FIX: NEVER set RectangleWidth=0. AutoCAD DWG specification states:
                // "If Width=0.0, word wrap is currently disabled." Setting Width=0
                // causes multi-line text to render as a single unbroken line.
                // Instead, use a content-aware clamped width matching the online path.
                if (widthRatio < 0.70)
                {
                    // Content is much narrower — use content-based width
                    double contentWidth = maxLineWidth * 1.25;
                    mtext.RectangleWidth = Math.Clamp(contentWidth,
                        originalRectWidth * 0.50,
                        originalRectWidth * 0.95);
                }
                else if (widthRatio < 0.95)
                {
                    // Slightly narrower — reduce width proportionally
                    double targetWidth = Math.Max(effectiveWidth * 1.08, originalRectWidth * 0.60);
                    mtext.RectangleWidth = targetWidth;
                }
                else
                {
                    double clampedRatio = Math.Clamp(widthRatio, 0.70, 1.30);
                    mtext.RectangleWidth = originalRectWidth * clampedRatio;
                }
                // else: text fills the rectangle well — keep original width

                // Estimate line count in the effective rectangle width
                double rectWidth = mtext.RectangleWidth > 0 ? mtext.RectangleWidth : effectiveWidth * 1.1;
                int estimatedLines = 0;
                foreach (var line in logicalLines)
                {
                    double lineW = EstimateTextWidth(line, currentHeight);
                    // Add 5% tolerance to Ceiling to prevent 1% overflow being counted as an extra line
                    estimatedLines += Math.Max(1, (int)Math.Ceiling(lineW / (rectWidth * 1.05)));
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
                        // Raised from 0.40 to 0.50 (matches updated collision avoidance floor)
                        double minHeight = originalHeight * 0.50;
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
                // Original was FREE width: only set a rectangle when truly necessary.
                // Use 80% (was 65%) of effective width for a more natural wrapping point.
                if (effectiveWidth > originalWidth * 1.3)
                {
                    double maxAllowable = originalWidth * 1.3;
                    double targetWidth = Math.Min(maxAllowable, Math.Max(originalWidth * 1.05, effectiveWidth * 0.80));
                    mtext.RectangleWidth = targetWidth;
                }
                // else: keep free width for natural tight spacing
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
    /// Detects rectangular frame boundaries by scanning ModelSpace, all PaperSpace
    /// layouts, AND block definitions (for frames stored as INSERT/CadInsert entities
    /// — the standard pattern in 85-95% of professional DWG files).
    ///
    /// Accepts closed LwPolylines with 4-8 vertices (allows chamfered corners, matching
    /// the online path) and validates approximate rectangular shape via angle checks.
    /// Recursively scans CadInsert entities to find frame polylines nested inside block
    /// definitions, transforming bounds to world coordinates.
    /// </summary>
    private static List<(double minX, double minY, double maxX, double maxY)> DetectFrames(CadDocument doc)
    {
        var frames = new List<(double, double, double, double)>();
        try
        {
            // Collect all entity collections to scan:
            // (a) ModelSpace, (b) PaperSpace layouts, (c) user-defined block records
            var collectionsToScan = new List<(IEnumerable<CadEntity> entities, string source)>();

            collectionsToScan.Add((doc.ModelSpace.Entities, "ModelSpace"));

            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock?.Entities != null)
                    collectionsToScan.Add((layout.AssociatedBlock.Entities, $"Layout:{layout.Name}"));
            }

            // Also scan user-defined block records for frame polylines that may be
            // referenced by CadInsert entities in ModelSpace/PaperSpace.
            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Entities != null && blockRecord.Entities.Count > 0)
                    collectionsToScan.Add((blockRecord.Entities, $"Block:{blockRecord.Name}"));
            }

            foreach (var (entities, source) in collectionsToScan)
            {
                ScanEntitiesForFrames(entities, frames, doc, 0);
            }

            if (frames.Count > 0)
                Log.Information("DetectFrames: found {Count} frame(s) across {Sources} source(s)",
                    frames.Count, collectionsToScan.Count);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Frame detection encountered an error (non-fatal)");
        }
        return frames;
    }

    /// <summary>
    /// Scans a collection of entities for frame-like closed LwPolylines.
    /// Recursively enters CadInsert entities to find frames inside block definitions
    /// (up to maxDepth=5 to prevent infinite recursion from circular references).
    /// </summary>
    private static void ScanEntitiesForFrames(
        IEnumerable<CadEntity> entities,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        CadDocument doc,
        int depth,
        double insertX = 0, double insertY = 0,
        double scaleX = 1, double scaleY = 1,
        double rotation = 0)
    {
        const int maxDepth = 5;
        if (depth > maxDepth) return;

        foreach (var entity in entities)
        {
            if (entity == null) continue;

            // ── Recurse into CadInsert (BlockReference) ──
            if (entity is CadInsert insert)
            {
                // Find the block definition by name
                var blockDef = doc.BlockRecords.FirstOrDefault(
                    b => string.Equals(b.Name, insert.Block?.Name, StringComparison.OrdinalIgnoreCase));
                if (blockDef != null && blockDef.Entities != null)
                {
                    // Accumulate transform: parent → this insert
                    double cosR = Math.Cos(insert.Rotation);
                    double sinR = Math.Sin(insert.Rotation);
                    double newX = insertX + insert.InsertPoint.X * scaleX;
                    double newY = insertY + insert.InsertPoint.Y * scaleY;
                    double newSx = scaleX * insert.XScale;
                    double newSy = scaleY * insert.YScale;
                    double newRot = rotation + insert.Rotation;

                    ScanEntitiesForFrames(blockDef.Entities, frames, doc,
                        depth + 1, newX, newY, newSx, newSy, newRot);
                }
                continue;
            }

            // ── Check for frame LwPolyline ──
            if (entity is CadLwPolyline poly && poly.IsClosed)
            {
                int n = poly.Vertices.Count;
                if (n < 4 || n > 8) continue; // Match online path: 4-8 vertices

                // Basic rectangular validation: check vertex angles
                if (!IsApproximatelyRectangular(poly)) continue;

                double minX = poly.Vertices.Min(v => v.Location.X);
                double minY = poly.Vertices.Min(v => v.Location.Y);
                double maxX = poly.Vertices.Max(v => v.Location.X);
                double maxY = poly.Vertices.Max(v => v.Location.Y);
                double area = (maxX - minX) * (maxY - minY);

                if (area <= MinFrameAreaSquareUnits) continue;

                // Apply accumulated CadInsert transform to convert from
                // block-local coordinates to world coordinates.
                if (depth > 0)
                {
                    double cosR = Math.Cos(rotation);
                    double sinR = Math.Sin(rotation);

                    // Transform all 4 corners (handles rotation correctly)
                    (double x, double y) TransformCorner(double x, double y)
                    {
                        double sx = x * scaleX;
                        double sy = y * scaleY;
                        double rx = sx * cosR - sy * sinR;
                        double ry = sx * sinR + sy * cosR;
                        return (rx + insertX, ry + insertY);
                    }

                    var c1 = TransformCorner(minX, minY);
                    var c2 = TransformCorner(maxX, maxY);
                    var c3 = TransformCorner(minX, maxY);
                    var c4 = TransformCorner(maxX, minY);

                    double wMinX = Math.Min(Math.Min(c1.x, c2.x), Math.Min(c3.x, c4.x));
                    double wMinY = Math.Min(Math.Min(c1.y, c2.y), Math.Min(c3.y, c4.y));
                    double wMaxX = Math.Max(Math.Max(c1.x, c2.x), Math.Max(c3.x, c4.x));
                    double wMaxY = Math.Max(Math.Max(c1.y, c2.y), Math.Max(c3.y, c4.y));

                    frames.Add((wMinX, wMinY, wMaxX, wMaxY));
                }
                else
                {
                    frames.Add((minX, minY, maxX, maxY));
                }
            }
        }
    }

    /// <summary>
    /// Quick rectangular validation for LwPolylines: checks that all interior
    /// angles are approximately 90 degrees (dot product < 0.15 threshold).
    /// Shared by the offline frame detector.
    /// </summary>
    private static bool IsApproximatelyRectangular(CadLwPolyline poly)
    {
        int n = poly.Vertices.Count;
        if (n < 4) return false;

        for (int i = 0; i < n; i++)
        {
            var prev = poly.Vertices[(i - 1 + n) % n].Location;
            var curr = poly.Vertices[i].Location;
            var next = poly.Vertices[(i + 1) % n].Location;

            double v1x = curr.X - prev.X;
            double v1y = curr.Y - prev.Y;
            double v2x = next.X - curr.X;
            double v2y = next.Y - curr.Y;

            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);

            if (len1 < 0.001 || len2 < 0.001) continue; // Skip tiny segments (fillets/chamfers)

            double dot = Math.Abs(v1x * v2x + v1y * v2y) / (len1 * len2);
            if (dot > 0.15) return false; // Not close to 90 degrees
        }
        return true;
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
    /// attachment point, rectangle width, and estimated height.
    /// Now correctly handles ALL 9 AutoCAD attachment point types (was hardcoded to TopLeft).
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

        // Compute bounds based on the 9 possible attachment points
        return mtext.AttachmentPoint switch
        {
            // Top row: text extends DOWNWARD
            AttachmentPointType.TopLeft => (x, y - totalHeight, x + w, y),
            AttachmentPointType.TopCenter => (x - w / 2, y - totalHeight, x + w / 2, y),
            AttachmentPointType.TopRight => (x - w, y - totalHeight, x, y),

            // Middle row: text extends UPWARD and DOWNWARD
            AttachmentPointType.MiddleLeft => (x, y - totalHeight / 2, x + w, y + totalHeight / 2),
            AttachmentPointType.MiddleCenter => (x - w / 2, y - totalHeight / 2, x + w / 2, y + totalHeight / 2),
            AttachmentPointType.MiddleRight => (x - w, y - totalHeight / 2, x, y + totalHeight / 2),

            // Bottom row: text extends UPWARD
            AttachmentPointType.BottomLeft => (x, y, x + w, y + totalHeight),
            AttachmentPointType.BottomCenter => (x - w / 2, y, x + w / 2, y + totalHeight),
            AttachmentPointType.BottomRight => (x - w, y, x, y + totalHeight),

            _ => (x, y - totalHeight, x + w, y), // Default: TopLeft
        };
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
    /// Uses a capped tolerance: 0.5% of frame dimension, but at most 3.0 units
    /// and at least 0.5 unit. This prevents large frames (A0: 1189 units) from
    /// allowing 11.9-unit overflows (was 1% = too permissive), while keeping
    /// reasonable tolerance for small frames.
    /// </summary>
    private static bool ExceedsFrame(
        (double minX, double minY, double maxX, double maxY) bounds,
        (double minX, double minY, double maxX, double maxY) frame)
    {
        double frameW = frame.maxX - frame.minX;
        double frameH = frame.maxY - frame.minY;
        double tolX = Math.Clamp(frameW * 0.005, 0.5, 3.0);
        double tolY = Math.Clamp(frameH * 0.005, 0.5, 3.0);
        return bounds.minX < frame.minX - tolX
            || bounds.maxX > frame.maxX + tolX
            || bounds.minY < frame.minY - tolY
            || bounds.maxY > frame.maxY + tolY;
    }

    // ───────────────────── Entity-level collision detection ─────────────────────

    /// <summary>
    /// Estimates the axis-aligned bounding box of any CAD entity for collision detection.
    /// Now supports 10+ entity types (was 4). Previously Line, Arc, Circle, Spline, Hatch
    /// all returned null — making 60-80% of drawing geometry invisible to collision detection.
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
                return EstimateInsertBounds(insert);

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

            case ACadSharp.Entities.Line line:
                return (
                    Math.Min(line.StartPoint.X, line.EndPoint.X),
                    Math.Min(line.StartPoint.Y, line.EndPoint.Y),
                    Math.Max(line.StartPoint.X, line.EndPoint.X),
                    Math.Max(line.StartPoint.Y, line.EndPoint.Y));

            case ACadSharp.Entities.Arc arc:
                // Arc MUST come before Circle — in ACadSharp, Arc extends Circle.
                // Approximate arc bounds by sampling points along the arc.
                // For collision detection, a slight over-estimate is safe (may cause
                // a bit more scaling but prevents missed collisions).
                return EstimateArcBounds(arc);

            case ACadSharp.Entities.Circle circle:
                double r = circle.Radius;
                return (
                    circle.Center.X - r, circle.Center.Y - r,
                    circle.Center.X + r, circle.Center.Y + r);

            case ACadSharp.Entities.Spline spline:
                // Use control points for a conservative bounding box.
                if (spline.ControlPoints.Count > 0)
                {
                    double sminX = spline.ControlPoints.Min(p => p.X);
                    double sminY = spline.ControlPoints.Min(p => p.Y);
                    double smaxX = spline.ControlPoints.Max(p => p.X);
                    double smaxY = spline.ControlPoints.Max(p => p.Y);
                    return (sminX, sminY, smaxX, smaxY);
                }
                return null;

            // Hatch, Solid, Polyline2D, Polyline3D — bounds are complex to compute
            // accurately in the offline path. They are logged below for awareness
            // but return null so ScaleDownToAvoidCollisions can focus on known types.
            default:
                return null;
        }
    }

    /// <summary>
    /// Estimates the bounding box of a CadInsert by computing the transformed
    /// bounds of all entities in the referenced block definition.
    /// Previously used a hardcoded 80×80 units which was completely arbitrary
    /// and wrong for any real-world block size.
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY)? EstimateInsertBounds(CadInsert insert)
    {
        try
        {
            // Try to get bounds from the block definition
            var blockDef = insert.Block;
            if (blockDef?.Entities == null || blockDef.Entities.Count == 0)
            {
                // Fallback: use scaled extent (better than hardcoded 80×80)
                double fallbackW = Math.Abs(insert.XScale) * 100;
                double fallbackH = Math.Abs(insert.YScale) * 100;
                return (insert.InsertPoint.X, insert.InsertPoint.Y,
                        insert.InsertPoint.X + fallbackW, insert.InsertPoint.Y + fallbackH);
            }

            // Compute the AABB of all entities in the block, then transform
            double? gMinX = null, gMinY = null, gMaxX = null, gMaxY = null;
            foreach (var blkEntity in blockDef.Entities)
            {
                var b = GetEntityBounds(blkEntity);
                if (!b.HasValue) continue;
                if (!gMinX.HasValue || b.Value.minX < gMinX) gMinX = b.Value.minX;
                if (!gMinY.HasValue || b.Value.minY < gMinY) gMinY = b.Value.minY;
                if (!gMaxX.HasValue || b.Value.maxX > gMaxX) gMaxX = b.Value.maxX;
                if (!gMaxY.HasValue || b.Value.maxY > gMaxY) gMaxY = b.Value.maxY;
            }

            if (!gMinX.HasValue)
            {
                double fw = Math.Abs(insert.XScale) * 100;
                double fh = Math.Abs(insert.YScale) * 100;
                return (insert.InsertPoint.X, insert.InsertPoint.Y,
                        insert.InsertPoint.X + fw, insert.InsertPoint.Y + fh);
            }

            // Apply the insert transform to the block bounds
            double cosR = Math.Cos(insert.Rotation);
            double sinR = Math.Sin(insert.Rotation);
            double sx = insert.XScale;
            double sy = insert.YScale;
            double ix = insert.InsertPoint.X;
            double iy = insert.InsertPoint.Y;

            (double x, double y) Transform(double bx, double by)
            {
                double tx = bx * sx;
                double ty = by * sy;
                return (tx * cosR - ty * sinR + ix,
                        tx * sinR + ty * cosR + iy);
            }

            // All four bounds are guaranteed non-null here: the early return above
            // (if (!gMinX.HasValue)) already handled the null case.
            double minX = gMinX!.Value, minY = gMinY!.Value;
            double maxX = gMaxX!.Value, maxY = gMaxY!.Value;
            var c1 = Transform(minX, minY);
            var c2 = Transform(maxX, maxY);
            var c3 = Transform(minX, maxY);
            var c4 = Transform(maxX, minY);

            return (
                Math.Min(Math.Min(c1.x, c2.x), Math.Min(c3.x, c4.x)),
                Math.Min(Math.Min(c1.y, c2.y), Math.Min(c3.y, c4.y)),
                Math.Max(Math.Max(c1.x, c2.x), Math.Max(c3.x, c4.x)),
                Math.Max(Math.Max(c1.y, c2.y), Math.Max(c3.y, c4.y)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Estimates the bounding box of an arc by sampling points at key angles
    /// (start, end, and the 4 quadrant boundaries: 0°, 90°, 180°, 270°).
    /// </summary>
    private static (double minX, double minY, double maxX, double maxY) EstimateArcBounds(ACadSharp.Entities.Arc arc)
    {
        double cx = arc.Center.X;
        double cy = arc.Center.Y;
        double r = arc.Radius;

        // Collect points at start, end, and quadrant boundaries
        var angles = new List<double> { arc.StartAngle, arc.EndAngle };
        // Add quadrant angles that fall within the arc sweep
        double sweep = arc.EndAngle - arc.StartAngle;
        if (sweep < 0) sweep += 2 * Math.PI;

        for (double a = 0; a < 2 * Math.PI; a += Math.PI / 2)
        {
            double da = a - arc.StartAngle;
            if (da < 0) da += 2 * Math.PI;
            if (da <= sweep)
                angles.Add(a);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (double a in angles)
        {
            double px = cx + r * Math.Cos(a);
            double py = cy + r * Math.Sin(a);
            if (px < minX) minX = px; if (px > maxX) maxX = px;
            if (py < minY) minY = py; if (py > maxY) maxY = py;
        }
        return (minX, minY, maxX, maxY);
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

        // Raised from 0.40 to 0.50 — 0.40 produced unreadable text. The offline
        // path has less accurate bounds (estimated, not geometric) so a slightly
        // lower floor than online (0.80) is acceptable, but 0.40 was too aggressive.
        double minHeight = originalHeight * 0.50;
        if (currentHeight <= minHeight) return;

        // Proportional collision margin (matches online path: originalHeight * 0.65)
        // instead of the previous hardcoded 2.0 units which was too large for
        // small text and too small for large text.
        double collisionMargin = originalHeight * 0.65;

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
            if (HasBoundsOverlap(currentBounds.Value, ob, collisionMargin))
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
            // Early exit when converged
            if (hi - lo < 0.005) break;

            double mid = (lo + hi) / 2;
            TrySetEntityHeight(targetEntity, mid);

            var testBounds = GetEntityBounds(targetEntity);
            if (!testBounds.HasValue) break;

            bool midHasCollision = false;
            foreach (var ob in otherBounds)
            {
                if (HasBoundsOverlap(testBounds.Value, ob, collisionMargin))
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
