using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Core.Models;
using DwgTranslator.Cad;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Tracks a successfully-written entity for post-write iterative collision resolution.
/// </summary>
internal sealed record WrittenEntity(
    Entity Entity,
    BlockTableRecord Block,
    double OriginalHeight,
    Extents3d? Frame);

/// <summary>
/// High-precision DWG writeback engine using AutoCAD .NET API.
/// Uses exact GeometricExtents for layout optimization and iterative collision resolution.
///
/// Writeback pipeline (3 phases):
/// 1. WRITE: Set all translated text + font + initial layout
/// 2. RESOLVE: Iterative collision detection → scaling → re-check (up to 3 passes)
/// 3. COMMIT: Save all changes
///
/// Key design principles:
/// - NEVER displace text from its original position (preserve coordinates)
/// - Only SCALE to resolve collisions (never move)
/// - Iterate until clean (check → adjust → re-check)
/// </summary>
public class AcadWriterEngine
{
    /// <summary>
    /// Writes translated text back into a DWG file using AutoCAD's native Database API.
    /// This overload opens the file from disk (side database) — avoid using when the file
    /// is already open in AutoCAD, as it may cause eFilerError due to file locks.
    /// </summary>
    public CadWriteResult WriteTranslations(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities,
        bool cnToEn = true)
    {
        var result = new CadWriteResult();

        if (!File.Exists(sourceFilePath))
        {
            result.Errors.Add($"Source file not found: {sourceFilePath}");
            Log.Error("Source DWG file not found: {Path}", sourceFilePath);
            return result;
        }

        try
        {
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(sourceFilePath, FileOpenMode.OpenForReadAndAllShare, false, null);

                WriteTranslationsToDatabase(db, entities, cnToEn, result);

                var outputDir = Path.GetDirectoryName(outputFilePath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                db.SaveAs(outputFilePath, DwgVersion.Current);
                Log.Information("AcadWriter: saved DWG to {Path}", outputFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AcadWriterEngine failed for {File}", sourceFilePath);
            result.Errors.Add($"Writeback error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Writes translated text directly into an already-open AutoCAD database.
    /// Use this overload when running from inside AutoCAD (e.g. from DWGTRANSLATEWRITE command)
    /// to avoid file-lock conflicts that cause eFilerError.
    /// The caller is responsible for saving the database afterward.
    /// </summary>
    public CadWriteResult WriteTranslations(
        Database db,
        List<TextEntity> entities,
        bool cnToEn = true)
    {
        var result = new CadWriteResult();
        try
        {
            WriteTranslationsToDatabase(db, entities, cnToEn, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AcadWriterEngine failed on active database");
            result.Errors.Add($"Writeback error: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Core writeback logic shared by both overloads.
    ///
    /// 3-phase pipeline:
    /// 1. WRITE: Set translated text + font + layout for all entities
    /// 2. RESOLVE: Iterative collision detection (frame + cross-layer + entity-overlap)
    ///    — only scales, never moves position
    /// 3. COMMIT: Save all changes in one transaction
    /// </summary>
    private void WriteTranslationsToDatabase(
        Database db,
        List<TextEntity> entities,
        bool cnToEn,
        CadWriteResult result)
    {
        // Detect frame boundaries before modifications
        var frames = FrameDetector.DetectFrames(db);
        Log.Information("AcadWriter: detected {Count} frame(s)", frames.Count);

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var entityMap = entities
                .Where(e => e.Status == TranslationStatus.Translated ||
                            e.Status == TranslationStatus.Reviewed)
                .ToDictionary(e => e.Handle, StringComparer.OrdinalIgnoreCase);

            int successCount = 0;
            var unprocessed = new HashSet<string>(entityMap.Keys, StringComparer.OrdinalIgnoreCase);
            var writtenEntities = new List<WrittenEntity>();

            // ── PHASE 1: Write all translations ──
            // Model space
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForWrite);
            var errors = new List<string>();
            successCount += ProcessBlockTableRecord(modelSpace, tr, db, entityMap, unprocessed, frames, cnToEn, errors, writtenEntities);
            result.Errors.AddRange(errors);

            // Paper space layouts and user blocks
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                string name = btr.Name;

                bool isModelSpace = name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase);
                bool isPaperSpace = name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase);

                if (!isModelSpace)
                {
                    if (isPaperSpace || (!btr.IsAnonymous && !name.StartsWith("*")))
                    {
                        successCount += ProcessBlockTableRecord(btr, tr, db, entityMap, unprocessed, frames, cnToEn, errors, writtenEntities);
                    }
                }
            }

            result.SuccessCount = successCount;
            result.FailCount = unprocessed.Count;

            if (unprocessed.Count > 0)
            {
                Log.Warning("AcadWriter: skipped {Count} unmatched entities", unprocessed.Count);
            }

            // ── PHASE 2: Iterative collision resolution ──
            // Runs check→adjust→re-check cycle. Each entity is checked against:
            // (a) frame boundaries, (b) cross-layer existing geometry,
            // (c) other translated text entities.
            // Only SCALES text — never moves it from its original position.
            IterativelyResolveAllCollisions(writtenEntities, tr, db);

            // ── PHASE 3: Commit ──
            tr.Commit();
        }
    }

    private int ProcessBlockTableRecord(
        BlockTableRecord btr,
        Transaction tr,
        Database db,
        Dictionary<string, TextEntity> entityMap,
        HashSet<string> unprocessed,
        List<Extents3d> frames,
        bool cnToEn,
        List<string> errors,
        List<WrittenEntity> writtenEntities)
    {
        int count = 0;
        foreach (ObjectId id in btr)
        {
            if (!id.IsValid) continue;

            Entity? entity = null;
            try
            {
                entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
            }
            catch (Exception ex)
            {
                if (entityMap.ContainsKey(id.Handle.ToString()))
                    errors.Add($"GetObject failed for Handle={id.Handle}: {ex.Message}");
                continue;
            }

            if (entity == null) continue;

            var handleStr = entity.Handle.ToString();
            if (entityMap.TryGetValue(handleStr, out var textEntity))
            {
                if (ReplaceEntity(entity, textEntity, frames, cnToEn, tr, btr, db, writtenEntities))
                {
                    count++;
                    unprocessed.Remove(handleStr);
                }
            }

            // Nested block references with attributes
            if (entity is BlockReference br)
            {
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (!attId.IsValid) continue;
                    AttributeReference? att = null;
                    try
                    {
                        att = tr.GetObject(attId, OpenMode.ForWrite, false) as AttributeReference;
                    }
                    catch { continue; }

                    if (att == null) continue;

                    var compoundHandle = $"{br.Handle}/{att.Tag}";
                    if (entityMap.TryGetValue(compoundHandle, out var attEntity))
                    {
                        try
                        {
                            att.TextString = attEntity.TranslatedText;
                            // AttributeReference is NOT tracked for collision resolution —
                            // attributes are part of block inserts and don't need independent
                            // geometry collision checking.
                            count++;
                            unprocessed.Remove(compoundHandle);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to update attribute {Tag}", att.Tag);
                        }
                    }
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Phase 1: Write translated text + font + layout optimization.
    /// Does NOT perform collision detection — that's done in Phase 2 (iterative).
    /// </summary>
    private bool ReplaceEntity(Entity entity, TextEntity ourEntity, List<Extents3d> frames, bool cnToEn, Transaction tr, BlockTableRecord btr, Database db, List<WrittenEntity> writtenEntities)
    {
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;

            double originalHeight;
            Extents3d? closestFrame;

            switch (entity)
            {
                case DBText dbText:
                    dbText.TextString = translatedText;
                    MapFont(dbText, ourEntity.TextStyleName, cnToEn, tr);
                    dbText.RecordGraphicsModified(true);
                    closestFrame = CollisionDetector.FindClosestFrame(dbText.Position, frames);
                    LayoutOptimizer.OptimizeDBText(dbText, translatedText, ourEntity, closestFrame);
                    originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : dbText.Height;
                    // Track for iterative collision resolution (Phase 2)
                    writtenEntities.Add(new WrittenEntity(dbText, btr, originalHeight, closestFrame));
                    return true;

                case MText mtext:
                    // Preserve original line spacing
                    if (ourEntity.MTextLineSpacing > 0)
                        mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
                    if (ourEntity.MTextLineSpacingStyle > 0)
                        mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

                    // Prepare translated content.
                    // Only rebuild for hard \P breaks (preserve intentional line structure).
                    // For ALL other cases, use raw translated text — LayoutOptimizer will
                    // set the correct Width, and AutoCAD auto-wraps at that boundary.
                    // Do NOT pre-insert \P breaks: LayoutOptimizer changes Width afterward,
                    // making pre-computed break positions incorrect.
                    string contents;
                    if (ourEntity.MTextHasHardBreaks && ourEntity.MTextLineCount > 1)
                    {
                        contents = Core.Services.TextWidthEstimator.RebuildMTextWithLineBreaks(
                            translatedText, ourEntity.MTextLineCount, mtext.TextHeight);
                    }
                    else
                    {
                        contents = translatedText;
                    }

                    // Set Contents so LayoutOptimizer analyzes translated text
                    mtext.Contents = contents
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");

                    MapFont(mtext, ourEntity.TextStyleName, cnToEn, tr);

                    // LayoutOptimizer determines final Width and TextHeight
                    closestFrame = CollisionDetector.FindClosestFrame(mtext.Location, frames);
                    LayoutOptimizer.OptimizeMText(mtext, contents, ourEntity, closestFrame, tr);

                    // CRITICAL: disable dynamic columns AFTER Width is set.
                    // AutoCAD docs: "You MUST set Width > 0 before calling ColumnType.
                    // If Width=0, DynamicColumns stretches text to fill column width,
                    // causing excessive word spacing."
                    // Only disable DynamicColumns — preserve StaticColumns if the
                    // original MText intentionally used multi-column layout.
                    if (mtext.Width <= 0)
                        mtext.Width = Math.Max(ourEntity.OriginalWidth, 50.0);
                    if (mtext.ColumnType == ColumnType.DynamicColumns)
                        mtext.ColumnType = ColumnType.NoColumns;

                    // Re-assert Contents after all Width/ColumnType changes
                    mtext.Contents = contents
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");
                    mtext.RecordGraphicsModified(true);

                    originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : mtext.TextHeight;
                    // Track for iterative collision resolution (Phase 2)
                    writtenEntities.Add(new WrittenEntity(mtext, btr, originalHeight, closestFrame));
                    return true;

                case Dimension dim:
                    dim.DimensionText = translatedText;
                    // Dimensions aren't tracked for collision resolution (they're intentionally near text)
                    return true;

                case MLeader mLeader:
                    if (mLeader.MText != null)
                    {
                        mLeader.MText.Contents = translatedText
                            .Replace("\r\n", "\\P")
                            .Replace("\n", "\\P")
                            .Replace("\r", "\\P");
                        MapFont(mLeader.MText, ourEntity.TextStyleName, cnToEn, tr);
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to replace entity {Handle} type {Type}", entity.Handle, entity.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Phase 2: Iterative collision resolution.
    ///
    /// Runs check→adjust→re-check cycles for ALL written entities.
    /// Each iteration checks every entity against:
    ///   (a) Frame boundaries (scale down if exceeds frame)
    ///   (b) Cross-layer existing geometry (scale down if collides with lines, hatches, etc.)
    ///   (c) Other translated text entities (scale down if two translations overlap)
    ///
    /// ONLY scales entity size — NEVER displaces (preserves original coordinates).
    /// Up to 3 iterations; stops early if no more changes (stable state).
    ///
    /// CRITICAL: Uses we.OriginalHeight (TRUE un-scaled original) for min-height floor
    /// to prevent compounding scale-down across iterations. Each iteration's binary
    /// search upper bound is the CURRENT height (to only go down, not up).
    /// </summary>
    private static void IterativelyResolveAllCollisions(
        List<WrittenEntity> written, Transaction tr, Database db)
    {
        const int maxIterations = 3;
        const double frameMinRatio = 0.65;   // Frame: allow down to 65% of original (was 0.80, too conservative)
        const double geoMinRatio = 0.65;      // Geometry: 65% floor — background mask handles remainder
        const double overlapMinRatio = 0.65;  // Overlap: 65% floor — background mask prevents visual clash

        if (written.Count == 0) return;

        Log.Information("Phase 2: iterative collision resolution — {Count} entities to check", written.Count);

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            int frameResolved = 0, crossLayerResolved = 0, overlapResolved = 0;

            foreach (var we in written)
            {
                // Read CURRENT height (may have been scaled in a previous iteration)
                double curHeight;
                if (we.Entity is MText mt) curHeight = mt.TextHeight;
                else if (we.Entity is DBText dbt) curHeight = dbt.Height;
                else if (we.Entity is AttributeReference att) curHeight = att.Height;
                else continue;

                double trueOriginalHeight = we.OriginalHeight;

                // (a) FRAME BOUNDARY: scale if entity extends outside drawing frame.
                //     Uses frameMinRatio for floor (65% of TRUE original).
                if (we.Frame.HasValue)
                {
                    if (EnsureEntityFitsFrame(we.Entity, we.Frame.Value, curHeight, frameMinRatio))
                    {
                        frameResolved++;
                        // Re-read height after scaling
                        if (we.Entity is MText mt2) curHeight = mt2.TextHeight;
                        else if (we.Entity is DBText dbt2) curHeight = dbt2.Height;
                        else if (we.Entity is AttributeReference att2) curHeight = att2.Height;
                    }
                }

                // (b) CROSS-LAYER: resolve interference with existing geometry.
                //     ALWAYS run — the old guard "curHeight < trueOriginalHeight" skipped
                //     collision detection for the vast majority of entities whose height was
                //     unchanged by LayoutOptimizer, leaving text unprotected against nearby
                //     geometry (lines, polylines, hatches, other text, etc.).
                //     TryResolveCrossLayerCollisions internally binary-searches from curHeight
                //     down to the floor; it can handle any starting height correctly.
                {
                    bool resolved = CollisionDetector.TryResolveCrossLayerCollisions(
                        we.Entity, we.Block, tr, curHeight, trueOriginalHeight, geoMinRatio, db);
                    if (resolved)
                    {
                        crossLayerResolved++;
                        if (we.Entity is MText mt3) curHeight = mt3.TextHeight;
                        else if (we.Entity is DBText dbt3) curHeight = dbt3.Height;
                        else if (we.Entity is AttributeReference att3) curHeight = att3.Height;
                    }
                }

                // (c) MText WIDTH ADJUSTMENT: if height is at or near the floor, try changing
                //     the wrapping width to alter text aspect ratio without shrinking further.
                //     Also runs when there are geo colliders even above floor — width changes
                //     can reflow text to avoid narrow collision zones.
                if (we.Entity is MText mtw)
                {
                    var geoColliders = CollisionDetector.CollectEntityColliders(
                        mtw, we.Block, tr, trueOriginalHeight, db);
                    if (geoColliders.Count > 0)
                    {
                        // Compute actual geometric width for accurate test-width derivation.
                        double actualRectWidth;
                        try
                        {
                            mtw.RecordGraphicsModified(true);
                            var mtwBounds = CollisionDetector.GetCorrectedBounds(mtw);
                            actualRectWidth = mtwBounds.MaxPoint.X - mtwBounds.MinPoint.X;
                        }
                        catch { actualRectWidth = 0; }
                        if (CollisionDetector.TryResolveMTextByWidthAdjustment(
                            mtw, geoColliders, curHeight, trueOriginalHeight, actualRectWidth))
                            crossLayerResolved++; // Width adjustment resolved interference
                    }
                }

                // (d) ENTITY OVERLAPS: resolve interference with other translated entities.
                //     ALWAYS run — the old guard skipped overlap detection for most entities.
                {
                    var otherEntities = written
                        .Where(o => !ReferenceEquals(o, we))
                        .Select(o => o.Entity)
                        .ToList();
                    if (CollisionDetector.ResolveEntityOverlaps(
                        we.Entity, otherEntities, curHeight, trueOriginalHeight, overlapMinRatio))
                        overlapResolved++;
                }
            }

            // Log progress
            Log.Information("Iteration {Iter}: frame={Frame}, geo={Geo}, overlap={Overlap}",
                iteration + 1, frameResolved, crossLayerResolved, overlapResolved);

            int totalChanges = frameResolved + crossLayerResolved + overlapResolved;
            if (totalChanges == 0)
            {
                Log.Information("Iterative resolution: stable after {Count} pass(es)", iteration + 1);
                break;
            }
        }
    }

    private static void MapFont(DBText text, string originalStyleName, bool cnToEn, Transaction tr)
    {
        try
        {
            string? targetFont = FontMapper.MapFontName(originalStyleName, cnToEn);
            if (string.IsNullOrEmpty(targetFont)) return;

            var db = text.Database;
            if (db == null) return;

            var textStyleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            ObjectId styleId = ObjectId.Null;

            foreach (ObjectId id in textStyleTable)
            {
                if (!id.IsValid) continue;
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(style.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                {
                    styleId = id;
                    break;
                }
            }

            if (styleId == ObjectId.Null)
            {
                // Create new style with proper font file name
                textStyleTable.UpgradeOpen();
                var newStyle = new TextStyleTableRecord { Name = targetFont };
                bool isShx = targetFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
                if (isShx)
                {
                    newStyle.FileName = targetFont;
                    newStyle.BigFontFileName = string.Empty;
                }
                else
                {
                    // TrueType font — set via Font property so AutoCAD uses the
                    // correct system font instead of defaulting to txt.shx.
                    newStyle.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                        targetFont, /*bold*/false, /*italic*/false, 0, 0);
                }
                styleId = textStyleTable.Add(newStyle);
                tr.AddNewlyCreatedDBObject(newStyle, true);
            }

            text.TextStyleId = styleId;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for DBText style {Style}", originalStyleName);
        }
    }

    private static void MapFont(MText mtext, string originalStyleName, bool cnToEn, Transaction tr)
    {
        try
        {
            string? targetFont = FontMapper.MapFontName(originalStyleName, cnToEn);
            if (string.IsNullOrEmpty(targetFont)) return;

            var db = mtext.Database;
            if (db == null) return;

            var textStyleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            ObjectId styleId = ObjectId.Null;

            foreach (ObjectId id in textStyleTable)
            {
                if (!id.IsValid) continue;
                var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(style.Name, targetFont, StringComparison.OrdinalIgnoreCase))
                {
                    styleId = id;
                    break;
                }
            }

            if (styleId == ObjectId.Null)
            {
                textStyleTable.UpgradeOpen();
                var newStyle = new TextStyleTableRecord { Name = targetFont };
                bool isShx = targetFont.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);
                if (isShx)
                {
                    newStyle.FileName = targetFont;
                    newStyle.BigFontFileName = string.Empty;
                }
                else
                {
                    newStyle.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                        targetFont, /*bold*/false, /*italic*/false, 0, 0);
                }
                styleId = textStyleTable.Add(newStyle);
                tr.AddNewlyCreatedDBObject(newStyle, true);
            }

            mtext.TextStyleId = styleId;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for MText style {Style}", originalStyleName);
        }
    }

    /// <summary>
    /// Rebuilds MText content with \P line breaks to match the original line count.
    /// Delegates to the shared TextWidthEstimator for character-width-aware greedy distribution.
    /// </summary>
    private static string RebuildMTextWithLineBreaks(string translatedText, int targetLineCount, double textHeight = 1.0)
        => Core.Services.TextWidthEstimator.RebuildMTextWithLineBreaks(translatedText, targetLineCount, textHeight);

    /// <summary>
    /// Ensures a text entity fits within its assigned frame boundary by scaling it down.
    /// NEVER displaces text — only scales. This preserves original coordinates.
    ///
    /// Returns true if the entity was modified (scaled to fit frame).
    /// </summary>
    private static bool EnsureEntityFitsFrame(Entity textEntity, Extents3d frame, double originalHeight, double minHeightRatio = 0.65)
    {
        try
        {
            textEntity.RecordGraphicsModified(true);
            var bounds = textEntity.GeometricExtents;

            if (!CollisionDetector.ExceedsFrame(bounds, frame))
                return false; // Fits — done

            double frameW = frame.MaxPoint.X - frame.MinPoint.X;
            double frameH = frame.MaxPoint.Y - frame.MinPoint.Y;
            double textW = bounds.MaxPoint.X - bounds.MinPoint.X;
            double textH = bounds.MaxPoint.Y - bounds.MinPoint.Y;

            double scaleW = textW > 0 ? (frameW * 0.95) / textW : 1.0;
            double scaleH = textH > 0 ? (frameH * 0.95) / textH : 1.0;
            double scale = Math.Min(scaleW, scaleH);
            if (scale >= 1.0) return false;

            scale = Math.Max(scale, minHeightRatio);

            if (textEntity is MText mtext)
            {
                double newHeight = mtext.TextHeight * scale;
                if (newHeight < originalHeight * minHeightRatio)
                    newHeight = originalHeight * minHeightRatio;
                mtext.TextHeight = newHeight;

                if (mtext.Width <= 0 || mtext.Width > frameW * 0.92)
                    mtext.Width = frameW * 0.92;
            }
            else if (textEntity is DBText dbText)
            {
                double newHeight = dbText.Height * scale;
                if (newHeight < originalHeight * minHeightRatio)
                    newHeight = originalHeight * minHeightRatio;
                dbText.Height = newHeight;
            }
            else if (textEntity is AttributeReference attrRef)
            {
                double newHeight = attrRef.Height * scale;
                if (newHeight < originalHeight * minHeightRatio)
                    newHeight = originalHeight * minHeightRatio;
                attrRef.Height = newHeight;
            }

            textEntity.RecordGraphicsModified(true);
            Log.Debug("EnsureEntityFitsFrame: {Type} {Handle} scaled by {Scale:F2}",
                textEntity.GetType().Name, textEntity.Handle, scale);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("EnsureEntityFitsFrame failed for {Handle}: {Error}", textEntity.Handle, ex.Message);
            return false;
        }
    }
}
