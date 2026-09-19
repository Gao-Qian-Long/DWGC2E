#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.Geometry;
#else
using Autodesk.AutoCAD.Geometry;
#endif
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Cad;
using DwgTranslator.Cad.Replacement;

namespace DwgTranslator.Cad.Replacement;

internal class ReplacedEntityInfo
{
    public ObjectId EntityId { get; set; }
    public double OriginalHeight { get; set; }
    public BlockTableRecord? OwningBtr { get; set; }
    public bool EnvelopeFitSucceeded { get; set; }
    public Entity? OriginalSnapshot { get; set; }
    public string? UnfittedMTextContents { get; set; }
}

/// <summary>
/// High-precision DWG writeback engine using AutoCAD .NET API.
/// 2-phase pipeline: WRITE (set text+font) 鈫?COMMIT (save).
/// </summary>
public class AcadWriterEngine
{
    // Exhaustive collision detection scans the whole block table multiple times per
    // translated entity. On drawings with thousands of labels this is quadratic and
    // can take many minutes. LayoutOptimizer already constrains each replacement to
    // its original frame, so large jobs use the bounded fast path below.
    private const int ExhaustiveCollisionEntityLimit = 400;

    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool targetIsCjk = true, WritebackOptions? options = null)
    {
        var result = new CadWriteResult();
        string? tempOutputPath = null;

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
                if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(outputFilePath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Output must not overwrite the source drawing.");
                db.ReadDwgFile(sourceFilePath, FileOpenMode.OpenForReadAndAllShare, false, null);
                WriteTranslationsToDatabase(db, entities, targetIsCjk, result);

                var outputDir = Path.GetDirectoryName(outputFilePath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                var fileName = Path.GetFileNameWithoutExtension(outputFilePath);
                tempOutputPath = Path.Combine(
                    outputDir ?? string.Empty,
                    $".{fileName}.{Guid.NewGuid():N}.tmp.dwg");
                db.SaveAs(tempOutputPath, DwgVersion.Current);
                if (!File.Exists(tempOutputPath) || new FileInfo(tempOutputPath).Length == 0)
                    throw new IOException("AutoCAD produced an empty output file");

                DwgTranslator.Core.Services.SafeFileCommit.Commit(tempOutputPath, outputFilePath, options?.OverwriteExisting == true);
                tempOutputPath = null;
                Log.Information("AcadWriter: saved DWG to {Path}", outputFilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AcadWriterEngine failed for {File}", sourceFilePath);
            result.SuccessCount = 0;
            result.FailCount = entities.Count;
            result.Errors.Add($"Writeback error: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempOutputPath))
            {
                try { if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath); }
                catch (Exception cleanupEx) { Log.DebugCategorized("AcadWriterEngine", "Failed to remove temporary DWG {Path}: {Detail}", tempOutputPath, cleanupEx.Message); }
            }
        }

        return result;
    }

    public CadWriteResult WriteTranslations(Database db, List<TextEntity> entities, bool targetIsCjk = true)
    {
        var result = new CadWriteResult();
        try
        {
            WriteTranslationsToDatabase(db, entities, targetIsCjk, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AcadWriterEngine failed on active database");
            result.SuccessCount = 0;
            result.FailCount = entities.Count;
            result.Errors.Add($"Writeback error: {ex.Message}");
        }
        return result;
    }

    private void WriteTranslationsToDatabase(Database db, List<TextEntity> entities, bool targetIsCjk, CadWriteResult result)
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var entityMap = entities
                .Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                    or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
                .ToDictionary(e => e.Handle, StringComparer.OrdinalIgnoreCase);

            int successCount = 0;
            var unprocessed = new HashSet<string>(entityMap.Keys, StringComparer.OrdinalIgnoreCase);
            var layoutRejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var replacedEntities = new List<ReplacedEntityInfo>();
            // Every entity is owned by exactly one block table record, so the sweep below must reach
            // each ObjectId once. This set enforces that invariant instead of assuming it: a host
            // that enumerates one member from two containers can then no longer inflate
            // SuccessCount, duplicate the audit list, or register a second snapshot for an object.
            var processedEntities = new HashSet<ObjectId>();

            // Model space
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForWrite);
            var errors = new List<string>();
            successCount += ProcessBlockTableRecord(modelSpace, tr, entityMap, unprocessed, targetIsCjk, errors, layoutRejected, replacedEntities, processedEntities);


            // Match explicit extracted handles in every local definition, including *U
            // anonymous/dynamic blocks. Names are not a reliable writeback eligibility test.
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                if (btrId == modelSpaceId) continue;
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsFromExternalReference || btr.IsFromOverlayReference) continue;
                successCount += ProcessBlockTableRecord(btr, tr, entityMap, unprocessed,
                    targetIsCjk, errors, layoutRejected, replacedEntities, processedEntities);
            }
            if (unprocessed.Count > 0)
                Log.Warning("AcadWriter: skipped {Count} unmatched entities", unprocessed.Count);

            result.Errors.AddRange(errors);
            // Rebuild from the completed layout, not the original snapshot.
            // This catches two translations growing into the same previously empty gap.
            AvailableTextSpace.Refresh(tr);
            var conflicted = replacedEntities
                .Select(item => new
                {
                    Item = item,
                    Baseline = BaselineOf(item),
                    Issues = AvailableTextSpace.FindIntersections(
                            (Entity)tr.GetObject(item.EntityId, OpenMode.ForRead), tr, BaselineOf(item))
                        .Where(issue => issue.StartsWith("CONFLICT=", StringComparison.Ordinal))
                        .ToList()
                })
                .Where(x => x.Issues.Count > 0)
                .ToList();
            var unrecoverableConflicts = new List<string>();
            int shrinkResolved = 0;
            foreach (var conflict in conflicted)
            {
                var handle = conflict.Item.EntityId.Handle.ToString();
                if (conflict.Item.OriginalSnapshot == null)
                {
                    unrecoverableConflicts.AddRange(conflict.Issues);
                    continue;
                }

                var current = (Entity)tr.GetObject(conflict.Item.EntityId, OpenMode.ForWrite);

                // A translation that only collides because it is slightly too large can still
                // be written by shrinking it further. Reverting to the source text is the last
                // resort, not the first response: leaving a Chinese label on an exported
                // English drawing is worse than a slightly smaller English one. The envelope
                // fit only guarantees a label fits its OWN original box, so neighbouring
                // rotated labels whose axis-aligned ink boxes overlap land here.
                if (TryShrinkUntilClear(current, conflict.Item.OriginalHeight, tr, conflict.Baseline, out var shrinkSteps, conflict.Item.UnfittedMTextContents))
                {
                    shrinkResolved++;
                    if (shrinkSteps > 0)
                        Log.Warning("Rendered interference resolved by shrinking {Handle} in {Steps} step(s); translation kept",
                            handle, shrinkSteps);
                    else
                        Log.Information("Rendered interference cleared by a neighbouring shrink {Handle}; translation kept", handle);
                    continue;
                }

                current.CopyFrom(conflict.Item.OriginalSnapshot);
                current.RecordGraphicsModified(true);
                layoutRejected.Add(handle);
                successCount--;
                Log.Warning("Rendered interference rejected {Handle}; original entity restored: {Issues}",
                    handle, string.Join("; ", conflict.Issues.Take(3)));
            }
            foreach (var item in replacedEntities)
                item.OriginalSnapshot?.Dispose();
            if (unrecoverableConflicts.Count > 0)
                throw new InvalidOperationException("Rendered layout interference: " +
                    string.Join("; ", unrecoverableConflicts.Take(10)));

            result.SuccessCount = successCount;
            result.FailCount = unprocessed.Count + layoutRejected.Count;
            result.FailedHandles.AddRange(unprocessed.Concat(layoutRejected).Distinct(StringComparer.OrdinalIgnoreCase));
            result.SucceededHandles.AddRange(entityMap.Keys.Except(result.FailedHandles, StringComparer.OrdinalIgnoreCase));
            if (unprocessed.Count > 0) result.Errors.Add("Unmatched handles: " + string.Join(", ", unprocessed.Take(20)));
            if (result.SuccessCount == 0) throw new InvalidOperationException("No translation could be written.");

            if (layoutRejected.Count > 0)
            {
                // Log the source text next to each handle: a bare handle list is not
                // actionable for the user, who needs to know which labels stayed Chinese.
                var rejectedDetail = string.Join("; ", layoutRejected.Take(30).Select(h =>
                    entityMap.TryGetValue(h, out var rejectedEntity) ? $"{h}={rejectedEntity.PlainText}" : h));
                var detail = $"Original text preserved after layout rejection ({layoutRejected.Count}): {rejectedDetail}";
                result.Errors.Add(detail);
                Log.Warning("{Detail}", detail);
            }

            Log.Information("Measured envelope verification passed for {Count} replacements ({Shrunk} separated by shrinking, {Preserved} kept original)",
                replacedEntities.Count, shrinkResolved, layoutRejected.Count);

            tr.Commit();
        }
    }

    private int ProcessBlockTableRecord(
        BlockTableRecord btr, Transaction tr,
        Dictionary<string, TextEntity> entityMap, HashSet<string> unprocessed,
        bool targetIsCjk, List<string> errors, HashSet<string> layoutRejected,
        List<ReplacedEntityInfo> replacedEntities, HashSet<ObjectId> processedEntities)
    {
        int count = 0;
        AvailableTextSpace.Prepare(btr, tr);
        foreach (ObjectId id in btr)
        {
            if (!id.IsValid) continue;

            Entity? entity = null;
            try { entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
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
                bool firstVisit = processedEntities.Add(entity.ObjectId);
                bool wasAlreadyApplied = entity switch
                {
                    DBText d => string.Equals(d.TextString, textEntity.TranslatedText, StringComparison.Ordinal),
                    MText m => string.Equals(m.Contents, textEntity.TranslatedText, StringComparison.Ordinal),
                    Dimension dimension => string.Equals(dimension.DimensionText, textEntity.TranslatedText, StringComparison.Ordinal),
                    _ => false
                };
                Entity? originalSnapshot = wasAlreadyApplied ? null : entity.Clone() as Entity;
                entity.UpgradeOpen();
                if (ReplaceEntity(entity, textEntity, targetIsCjk, tr, out var envelopeFitSucceeded, out var unfittedMTextContents))
                {
                    if (!envelopeFitSucceeded)
                    {
                        Log.Warning("Layout fit rejected {Handle}: {Text}; height={Height}; width={Width}", handleStr,
                            textEntity.PlainText, entity is DBText d ? d.Height : entity is MText m ? m.TextHeight : 0,
                            entity is DBText d2 ? d2.WidthFactor : entity is MText m2 ? m2.Width : 0);
                        layoutRejected.Add(handleStr);
                        unprocessed.Remove(handleStr);
                        if (originalSnapshot != null) { entity.CopyFrom(originalSnapshot); entity.RecordGraphicsModified(true); }
                        originalSnapshot?.Dispose();
                        continue;
                    }
                    if (firstVisit) count++;
                    unprocessed.Remove(handleStr);
                    if (firstVisit && !wasAlreadyApplied)
                    {
                        replacedEntities.Add(new ReplacedEntityInfo
                        {
                            EntityId = entity.ObjectId,
                            UnfittedMTextContents = unfittedMTextContents,
                            OriginalHeight = textEntity.OriginalHeight > 0 ? textEntity.OriginalHeight : textEntity.Height,
                            OwningBtr = btr,
                            EnvelopeFitSucceeded = envelopeFitSucceeded,
                            OriginalSnapshot = originalSnapshot
                        });
                    }
                }
                else
                {
                    // The replacement failed part-way through (unsupported type, or an exception
                    // caught inside it). Put the source text back and record it as "kept original":
                    // leaving the handle in the unprocessed set aborted the entire transaction, so a
                    // single stubborn label used to discard every finished translation.
                    if (originalSnapshot != null)
                    {
                        entity.CopyFrom(originalSnapshot);
                        entity.RecordGraphicsModified(true);
                        originalSnapshot.Dispose();
                    }
                    Log.Warning("Replacement failed for {Handle}: {Text}; original text kept", handleStr, textEntity.PlainText);
                    layoutRejected.Add(handleStr);
                    unprocessed.Remove(handleStr);
                }
            }

            if (entity is BlockReference br)
            {
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    if (!attId.IsValid) continue;
                    AttributeReference? att = null;
                    try { att = tr.GetObject(attId, OpenMode.ForWrite, false) as AttributeReference; }
                    catch { continue; }
                    if (att == null) continue;

                    var compoundHandle = $"{br.Handle}/{att.Tag}";
                    if (entityMap.TryGetValue(compoundHandle, out var attEntity))
                    {
                        // The owning INSERT is visited once per writeback, so a second entry for the
                        // same attribute can only come from a host that enumerates one member from
                        // two containers; it must never inflate the counters or the audit list.
                        bool firstAttributeVisit = processedEntities.Add(att.ObjectId);
                        if (AttributeTranslationPolicy.IsMetadataTag(att.Tag))
                        {
                            // Accept legacy requests without mutating machine mappings
                            // or asking invisible attributes for geometric extents.
                            unprocessed.Remove(compoundHandle);
                            if (firstAttributeVisit) count++;
                            Log.Information("Preserved title-block metadata {Handle}", compoundHandle);
                            continue;
                        }
                        try
                        {
                            var origAttHeight = att.Height;
                            var origAttText = att.TextString;
                            var origAttWidthFactor = att.WidthFactor;
                            var origAttPosition = att.Position;
                            var origAttAlignment = att.AlignmentPoint;
                            var origAttStyle = att.TextStyleId;
                            bool unchanged = string.Equals(att.TextString, attEntity.TranslatedText, StringComparison.Ordinal);
                            Entity? originalSnapshot = unchanged ? null : att.Clone() as Entity;
                            Extents3d? originalBounds = TryGetEntityBounds(att);
                            if (originalBounds.HasValue && !att.Invisible)
                                originalBounds = AvailableTextSpace.Measure(att, originalBounds.Value, tr,
                                    false, out _, preserveSourceFootprint: false);
                            att.TextString = attEntity.TranslatedText;
                            bool envelopeFitSucceeded = unchanged || FitDbTextToOriginalEnvelope(
                                att, originalBounds, origAttHeight, attEntity.Handle);
                            if (!envelopeFitSucceeded)
                            {
                                att.TextStyleId = origAttStyle;
                                att.TextString = origAttText;
                                att.Height = origAttHeight;
                                att.WidthFactor = origAttWidthFactor;
                                att.Position = origAttPosition;
                                att.AlignmentPoint = origAttAlignment;
                                att.RecordGraphicsModified(true);
                                layoutRejected.Add(compoundHandle);
                                unprocessed.Remove(compoundHandle);
                                originalSnapshot?.Dispose();
                                Log.Warning("Layout fit rejected attribute {Handle}; original text preserved", compoundHandle);
                                continue;
                            }
                            if (firstAttributeVisit) count++;
                            unprocessed.Remove(compoundHandle);
                            if (firstAttributeVisit && !unchanged)
                            {
                                replacedEntities.Add(new ReplacedEntityInfo
                                {
                                    EntityId = att.ObjectId,
                                    OriginalHeight = origAttHeight > 0 ? origAttHeight : 2.5,
                                    OwningBtr = btr,
                                    EnvelopeFitSucceeded = envelopeFitSucceeded,
                                    OriginalSnapshot = originalSnapshot
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            // This branch can have changed style/geometry before failing.
                            // Abort the transaction rather than committing an unverified partial mutation.
                            throw new InvalidOperationException("Attribute replacement could not be safely completed: " + compoundHandle, ex);
                        }
                    }
                }
            }

            if (entity is Table table && processedEntities.Add(table.ObjectId))
            {
                // Cell handles are compound ("{table}:{row}:{col}"), so a table's own ObjectId is
                // never an entityMap key and this guard cannot suppress a first-time visit.
                count += ProcessTableCells(table, entityMap, unprocessed, errors);
            }
        }
        return count;
    }

    private static int ProcessTableCells(
        Table table,
        Dictionary<string, TextEntity> entityMap,
        HashSet<string> unprocessed,
        List<string> errors)
    {
        int count = 0;
        var tableHandle = table.Handle.ToString();
        var keys = entityMap.Keys
            .Where(k => k.StartsWith(tableHandle + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            if (!entityMap.TryGetValue(key, out var textEntity)) continue;
            if (string.IsNullOrEmpty(textEntity.TranslatedText)) continue;

            var parts = key.Split(':');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[1], out var row) || !int.TryParse(parts[2], out var col)) continue;

            try
            {
                if (row < 0 || col < 0 || row >= table.Rows.Count || col >= table.Columns.Count)
                {
                    errors.Add($"Table cell out of range: {key}");
                    continue;
                }

                var cell = table.Cells[row, col];
                if (cell.Contents == null || cell.Contents.Count != 1)
                {
                    errors.Add($"Table cell has multiple contents and was preserved: {key}");
                    continue;
                }
                cell.Value = textEntity.TranslatedText;
                count++;
                unprocessed.Remove(key);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Table replacement could not be safely completed: " + key, ex);
            }
        }

        return count;
    }

    private static bool ReplaceEntity(
        Entity entity, TextEntity ourEntity, bool targetIsCjk, Transaction tr,
        out bool envelopeFitSucceeded, out string? unfittedMTextContents)
    {
        envelopeFitSucceeded = true;
        unfittedMTextContents = null;
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;
            if (entity is AttributeDefinition definition && !definition.Constant)
            {
                Log.Information("Preserved non-rendered attribute template {Handle}", ourEntity.Handle);
                return true;
            }
            // Column intent belongs to the SOURCE geometry, not to the target language. The old
            // targetIsCjk gate disabled this for the common ZH -> EN path, leaving vertical Chinese
            // labels horizontal (or reverting them after the resulting layout conflict).
            bool characterColumn = entity is MText sourceMText &&
                DwgTranslator.Core.Services.VerticalTextLayout.IsCharacterColumn(
                    ourEntity.PlainText, sourceMText.Rotation, sourceMText.Width, sourceMText.TextHeight);
            Extents3d? originalBounds = TryGetEntityBounds(entity);
            double readingLength = 0;
            if (originalBounds.HasValue)
                originalBounds = AvailableTextSpace.Measure(entity, originalBounds.Value, tr, characterColumn, out readingLength);

            switch (entity)
            {
                case DBText dbText:
                    if (string.Equals(dbText.TextString, translatedText, StringComparison.Ordinal)) return true;
                    var originalDbText = dbText.TextString;
                    var originalDbHeight = dbText.Height;
                    var originalDbWidthFactor = dbText.WidthFactor;
                    var originalDbPosition = dbText.Position;
                    var originalDbAlignment = dbText.AlignmentPoint;
                    var originalDbStyle = dbText.TextStyleId;
                    dbText.TextString = translatedText;
                    // Restore original text height before font mapping.
                    // Font mapping changes the TextStyleId; if the new text style
                    // has a fixed height, it would override the entity height.
                    if (ourEntity.Height > 0)
                        dbText.Height = ourEntity.Height;
                    AcadFontApplier.MapFont(dbText, ourEntity.TextStyleName, targetIsCjk, tr);
                    // Re-apply height after font mapping in case the new style
                    // has a fixed height that overrode our setting.
                    if (ourEntity.Height > 0)
                        dbText.Height = ourEntity.Height;
                    envelopeFitSucceeded = FitDbTextToOriginalEnvelope(
                        dbText, originalBounds,
                        ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height,
                        ourEntity.Handle);
                    if (!envelopeFitSucceeded)
                    {
                        dbText.TextStyleId = originalDbStyle;
                        dbText.TextString = originalDbText;
                        dbText.Height = originalDbHeight;
                        dbText.WidthFactor = originalDbWidthFactor;
                        dbText.Position = originalDbPosition;
                        dbText.AlignmentPoint = originalDbAlignment;
                    }
                    dbText.RecordGraphicsModified(true);
                    return true;

                case MText mtext:
                    if (string.Equals(mtext.Contents, translatedText, StringComparison.Ordinal)) return true;
                    var originalMTextContents = mtext.Contents;
                    var originalMTextHeight = mtext.TextHeight;
                    var originalMTextWidth = mtext.Width;
                    var originalMTextLocation = mtext.Location;
                    var originalMTextRotation = mtext.Rotation;
                    var originalMTextColumnType = mtext.ColumnType;
                    var originalMTextLineSpacing = mtext.LineSpacingFactor;
                    var originalMTextLineSpacingStyle = mtext.LineSpacingStyle;
                    var originalMTextStyle = mtext.TextStyleId;
                    if (ourEntity.MTextLineSpacing > 0)
                        mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
                    if (ourEntity.MTextLineSpacingStyle > 0)
                        mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

                    var layoutText = characterColumn
                        ? DwgTranslator.Core.Services.VerticalTextLayout.FormatTranslatedColumn(translatedText, targetIsCjk)
                        : translatedText;
                    mtext.Contents = DwgTranslator.Core.Services.FontMapper.MapInlineFonts(layoutText, targetIsCjk)
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");

                    unfittedMTextContents = mtext.Contents;
                    mtext.ColumnType = ColumnType.NoColumns;
                    if (characterColumn)
                    {
                        // Latin text uses a rotated coherent phrase; CJK stays upright and is
                        // stacked with explicit paragraph breaks by FormatTranslatedColumn.
                        mtext.Rotation = DwgTranslator.Core.Services.VerticalTextLayout.TargetRotation(
                            originalMTextRotation, targetIsCjk);
                    }

                    // Keep a non-zero wrap width when the source had a fixed rectangle.
                    // Width=0 disables AutoCAD word wrap and makes long EN text collide.
                    if (ourEntity.MTextRectangleWidth > 0)
                        mtext.Width = ourEntity.MTextRectangleWidth;

                    // Restore original text height
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    AcadFontApplier.MapFont(mtext, ourEntity.TextStyleName, targetIsCjk, tr);
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    // Fit against measured free space before reducing height.
                    // The legacy estimator reduced height before trying that width.
                    envelopeFitSucceeded = FitMTextToOriginalEnvelope(
                        mtext, originalBounds,
                        ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height,
                        ourEntity.Handle, characterColumn, readingLength);
                    if (!envelopeFitSucceeded)
                    {
                        mtext.TextStyleId = originalMTextStyle;
                        mtext.Contents = originalMTextContents;
                        mtext.TextHeight = originalMTextHeight;
                        mtext.Width = originalMTextWidth;
                        mtext.Location = originalMTextLocation;
                        mtext.Rotation = originalMTextRotation;
                        mtext.ColumnType = originalMTextColumnType;
                        mtext.LineSpacingFactor = originalMTextLineSpacing;
                        mtext.LineSpacingStyle = originalMTextLineSpacingStyle;
                    }
                    mtext.RecordGraphicsModified(true);
                    return true;

                case Dimension dim:
                    AcadFontApplier.MapFont(dim, ourEntity.TextStyleName, targetIsCjk, tr);
                    dim.DimensionText = translatedText;
                    return true;

                case MLeader mLeader:
                    // MText is a detached copy in the host API. Mutate one copy,
                    // assign it back, then dispose it rather than mutating getters.
                    using (var leaderText = mLeader.MText)
                    {
                        if (leaderText == null) return false;
                        if (string.Equals(leaderText.Contents, translatedText, StringComparison.Ordinal)) return true;
                        var originalMTextBounds = TryGetEntityBounds(leaderText);
                        leaderText.Contents = translatedText
                            .Replace("\r\n", "\\P")
                            .Replace("\n", "\\P")
                            .Replace("\r", "\\P");
                        AcadFontApplier.MapFont(leaderText, ourEntity.TextStyleName, targetIsCjk, tr);
                        LayoutOptimizer.OptimizeMText(leaderText, translatedText, ourEntity, originalMTextBounds, tr);
                        envelopeFitSucceeded = FitMTextToOriginalEnvelope(
                            leaderText, originalMTextBounds,
                            ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height,
                            ourEntity.Handle);
                        if (envelopeFitSucceeded)
                        {
                            mLeader.MText = leaderText;
                            mLeader.RecordGraphicsModified(true);
                        }
                        return true;
                    }

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

    private static Extents3d? TryGetEntityBounds(Entity entity)
    {
        try
        {
            entity.RecordGraphicsModified(true);
            return CollisionDetector.GetCorrectedBounds(entity);
        }
        catch (Exception ex)
        {
            Log.Warning("Original bounds unavailable {Handle}: {Detail}", entity.Handle.ToString(),
                entity.GetType().Name + "; visible=" + entity.Visible + "; " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Fits a translated DBText into the space measured for the original text.
    ///
    /// A DBText cannot wrap, so a longer translation inside a fixed-width cell cannot keep its
    /// original size. Instead of exhausting one knob -- which yields either flattened glyphs or
    /// much smaller text -- the fit searches the size space and keeps the candidate closest to the
    /// source size:
    ///   1. the source size, when it already fits: nothing is changed;
    ///   2. otherwise a UNIFORM scale, bisected to the largest value that still fits. Uniform
    ///      scaling is the only distortion-free option, so this is "as large as the cell allows";
    ///   3. only when even the smallest uniform scale fails, condense the glyphs horizontally.
    ///      That is the last resort before giving up, because a label left in the source language
    ///      reads worse than a condensed one.
    /// </summary>
    private static bool FitDbTextToOriginalEnvelope(
        DBText text, Extents3d? originalBounds, double originalHeight, string handle)
    {
        if (!originalBounds.HasValue) return false;
        var bounds = originalBounds.Value;

        double originalWidthFactor = text.WidthFactor;
        double originalTextHeight = text.Height;
        var startPosition = text.Position;
        var startAlignment = text.AlignmentPoint;
        double referenceHeight = originalHeight > 0 ? originalHeight : originalTextHeight;
        double floorScale = WritebackConstants.AbsoluteMinFitHeightRatio;
        int tries = 0;

        // scale = uniform size factor; widthRetention = fraction of the source width factor kept.
        // Every candidate starts from the original position, otherwise AlignInsideEnvelope's
        // translation would accumulate across attempts.
        void Apply(double scale, double widthRetention)
        {
            text.Height = referenceHeight * scale;
            text.WidthFactor = originalWidthFactor * widthRetention;
            text.Position = startPosition;
            text.AlignmentPoint = startAlignment;
            text.RecordGraphicsModified(true);
        }

        bool Fits()
        {
            tries++;
            try
            {
                var current = CollisionDetector.GetCorrectedBounds(text);
                current = AlignInsideEnvelope(text, current, bounds);
                return IsInsideOriginalEnvelope(current, bounds, referenceHeight);
            }
            catch { return false; }
        }

        // 1) Already fits at the source size.
        Apply(1.0, 1.0);
        if (Fits()) return true;

        // 2) Largest uniform scale that fits (the fit predicate is monotone in the scale).
        Apply(floorScale, 1.0);
        if (Fits())
        {
            double low = floorScale, high = 1.0;   // invariant: low fits, high does not
            for (int step = 0; step < 16; step++)
            {
                double mid = (low + high) / 2;
                Apply(mid, 1.0);
                if (Fits()) low = mid; else high = mid;
            }
            Apply(low, 1.0);
            if (Fits())
            {
                Log.Warning(
                    "Envelope fit DBText {Handle}: uniform scale to {Ratio:P0} of the original size after {Tries} tries (aspect preserved)",
                    handle, low, tries);
                return true;
            }
        }

        // 3) Last resort: keep the smallest uniform size and condense the glyphs as little as
        //    possible. At this point widthRetention = 1.0 is known not to fit, so the bisection
        //    starts from a valid invariant.
        double retentionLow = WritebackConstants.FallbackWidthFactorRetention, retentionHigh = 1.0;
        Apply(floorScale, retentionLow);
        if (Fits())
        {
            for (int step = 0; step < 16; step++)
            {
                double mid = (retentionLow + retentionHigh) / 2;
                Apply(floorScale, mid);
                if (Fits()) retentionLow = mid; else retentionHigh = mid;
            }
            Apply(floorScale, retentionLow);
            if (Fits())
            {
                Log.Warning(
                    "Envelope fit DBText {Handle}: condensed widthFactor {OldW:F3}->{NewW:F3} at {Scale:P0} of the original height after {Tries} tries",
                    handle, originalWidthFactor, originalWidthFactor * retentionLow, floorScale, tries);
                return true;
            }
        }

        Log.Warning(
            "Envelope fit DBText {Handle}: no fitting size found after {Tries} tries (uniform scale floor {Floor:P0}, condensation floor {Condense:P0}); original text kept",
            handle, tries, floorScale, WritebackConstants.FallbackWidthFactorRetention);
        Apply(1.0, 1.0);
        return false;
    }

    private static bool FitMTextToOriginalEnvelope(
        MText text, Extents3d? originalBounds, double originalHeight, string handle, bool singleLine = false,
        double readingLength = 0)
    {
        var contents=MTextFitFormatting.NormalizeHeights(text.Contents, originalHeight > 0 ? originalHeight : text.TextHeight);
        var location=text.Location;
        var height=text.TextHeight;
        var width=text.Width;
        var factors=new[]{1.0,.9,.8,.75,.65,.55,WritebackConstants.FallbackWidthFactorRetention};

        void Reset(double factor)
        {
            text.Location=location; text.TextHeight=height; text.Width=width;
            text.Contents=MTextFitFormatting.ScaleWidths(contents, factor);
            text.RecordGraphicsModified(true);
        }

        // Evaluate every factor instead of stopping at the first one that fits: the first fit can
        // be substantially smaller than a slightly more condensed one, which reads as "why is this
        // label so much smaller than its neighbours". Keep the tallest result, and prefer the
        // least condensed candidate when the heights are within 1%.
        double bestFactor=0, bestHeight=0;
        foreach(double factor in factors)
        {
            // Extra condensation is a last resort, not a way to enlarge already fitting labels.
            if (factor < .65 && bestFactor > 0) break;
            if (factor < .75 && bestHeight >= originalHeight * WritebackConstants.PreferredFitHeightRatio) break;
            Reset(factor);
            if(!FitMTextCandidate(text,originalBounds,originalHeight,handle,singleLine,readingLength))continue;

            double achieved=text.TextHeight;
            if(achieved>bestHeight*1.01 || (achieved>=bestHeight*0.99 && factor>bestFactor))
            {
                bestHeight=achieved; bestFactor=factor;
            }
        }

        if(bestFactor>0)
        {
            // Re-run the winner so the entity ends up in exactly the state that fitted.
            Reset(bestFactor);
            if(FitMTextCandidate(text,originalBounds,originalHeight,handle,singleLine,readingLength))
            {
                if(bestFactor<1.0)
                    Log.Warning("Envelope fit MText {Handle}: best of {Tries} layout tries = widthFactor {W:F2}, height {H:F3}",
                        handle,factors.Length,bestFactor,bestHeight);
                return true;
            }
        }

        Reset(1.0);
        return false;
    }

    private static bool FitMTextCandidate(
        MText text, Extents3d? originalBounds, double originalHeight, string handle, bool singleLine,
        double readingLength = 0)
    {
        if (!originalBounds.HasValue) return false;
        double startHeight = text.TextHeight;
        double startWidth = text.Width;

        // First try the full available wrap width at the original height. Only
        // shrink after measuring actual host glyphs; estimates over-wrap English.
        double angle = NormalizeHalfTurn(text.Rotation);
        if (Math.Abs(Math.Sin(angle)) < .001 || Math.Abs(Math.Cos(angle)) < .001)
        {
            var box = originalBounds.Value;
            double length = Math.Abs(Math.Cos(angle)) < .001
                ? box.MaxPoint.Y-box.MinPoint.Y : box.MaxPoint.X-box.MinPoint.X;
            text.Width = singleLine ? 0 : Math.Max(.01,length*.98);
            double referenceHeight = originalHeight > 0 ? originalHeight : startHeight;
            // Search down to the absolute floor, not just the preferred one: the largest
            // fitting height is what we want, and refusing anything below the preferred
            // ratio used to throw away an otherwise valid translation.
            double searchFloor = referenceHeight * WritebackConstants.AbsoluteMinFitHeightRatio;
            double low = searchFloor;
            double high = referenceHeight;
            double best = 0;
            for (int step=0; step<16; step++)
            {
                double candidate = step == 0 ? high : (low+high)/2;
                text.TextHeight = candidate;
                text.RecordGraphicsModified(true);
                var bounds = AlignInsideEnvelope(text, CollisionDetector.GetCorrectedBounds(text), box);
                if (IsInsideOriginalEnvelope(bounds,box,originalHeight))
                {
                    best = candidate;
                    low = candidate;
                    if (step==0) return true;
                }
                else high = candidate;
            }
            if (best > 0)
            {
                text.TextHeight = best;
                text.RecordGraphicsModified(true);
                var bounds = AlignInsideEnvelope(text,CollisionDetector.GetCorrectedBounds(text),box);
                if (IsInsideOriginalEnvelope(bounds,box,originalHeight))
                {
                    if (best < referenceHeight * WritebackConstants.PreferredFitHeightRatio - 1e-8)
                        Log.Warning("Envelope fit MText {Handle}: accepted shrunk height {OldH:F3}->{NewH:F3} ({Ratio:P0})",
                            handle, startHeight, best, referenceHeight > 0 ? best / referenceHeight : 0);
                    return true;
                }
            }
            Log.Warning(
                "Envelope fit MText {Handle}: giving up (no height in [{Floor:F3},{Ceil:F3}] fits the envelope); original text kept",
                handle, searchFloor, referenceHeight);
            text.TextHeight=startHeight;
            text.Width=startWidth;
            return false;
        }

        // Rotated text (any rotation that is not a multiple of 90 degrees).
        //
        // AvailableTextSpace.Measure now returns the corridor measured in the text's own frame, so
        // originalBounds is the space actually available along the reading direction. Search for the
        // largest fitting height, exactly like the axis-aligned branch above.
        {
            double boxW = Math.Max(originalBounds.Value.MaxPoint.X - originalBounds.Value.MinPoint.X, 0.001);
            double boxH = Math.Max(originalBounds.Value.MaxPoint.Y - originalBounds.Value.MinPoint.Y, 0.001);
            double rotation = NormalizeHalfTurn(text.Rotation);
            bool vertical = Math.Abs(Math.Sin(rotation)) > Math.Abs(Math.Cos(rotation));
            double originalLength = vertical ? boxH : boxW;
            // Wrap within the measured corridor, not within the axis-aligned bounding box. The box of
            // a rotated label is a diagonal square whose dimensions have nothing to do with the
            // reading direction, so using it produced a column far narrower than the free run beside
            // the label -- 停止指示 was broken over two lines at 44% height when a single full-height
            // line fitted in the corridor.
            double availableLength = readingLength > 0 ? readingLength : originalLength;
            if (text.Width <= 0 || text.Width > availableLength * 0.96)
                text.Width = Math.Max(availableLength * 0.96, 0.1);

            double ceiling = originalHeight > 0 ? originalHeight : startHeight;
            double floor = ceiling * WritebackConstants.AbsoluteMinFitHeightRatio;
            double searchFloor = floor;
            double searchCeiling = ceiling;
            double best = 0;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                double candidate = attempt == 0 ? ceiling : (floor + ceiling) / 2;
                text.TextHeight = candidate;
                text.RecordGraphicsModified(true);

                Extents3d current;
                try { current = CollisionDetector.GetCorrectedBounds(text); }
                catch { break; }

                current = AlignInsideEnvelope(text, current, originalBounds.Value);
                if (IsInsideOriginalEnvelope(current, originalBounds.Value, originalHeight))
                {
                    best = candidate;
                    floor = candidate;
                    if (attempt == 0)
                    {
                        Log.Information(
                            "Envelope fit MText {Handle}: fits at full height {Height:F3} (width {OldW:F3}->{NewW:F3})",
                            handle, candidate, startWidth, text.Width);
                        return true;
                    }
                }
                else ceiling = candidate;
            }

            if (best > 0)
            {
                text.TextHeight = best;
                text.RecordGraphicsModified(true);
                Extents3d finalBounds;
                try { finalBounds = AlignInsideEnvelope(text, CollisionDetector.GetCorrectedBounds(text), originalBounds.Value); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Envelope fit MText {Handle}: final bounds unavailable after fit", handle);
                    text.TextHeight = startHeight;
                    text.Width = startWidth;
                    return false;
                }
                if (IsInsideOriginalEnvelope(finalBounds, originalBounds.Value, originalHeight))
                {
                    if (best < originalHeight * WritebackConstants.PreferredFitHeightRatio - 1e-8)
                        Log.Warning(
                            "Envelope fit MText {Handle}: accepted shrunk height {OldH:F3}->{NewH:F3} ({Ratio:P0}); width {OldW:F3}->{NewW:F3}",
                            handle, startHeight, best, originalHeight > 0 ? best / originalHeight : 0,
                            startWidth, text.Width);
                    else if (best < startHeight - 1e-8)
                        Log.Information(
                            "Envelope fit MText {Handle}: width {OldW:F3}->{NewW:F3}, height {OldH:F3}->{NewH:F3}",
                            handle, startWidth, text.Width, startHeight, best);
                    return true;
                }
            }

            Log.Warning(
                "Envelope fit MText {Handle}: giving up (no height in [{Floor:F3},{Ceil:F3}] fits the original envelope); original text kept",
                handle, searchFloor, searchCeiling);
            text.TextHeight = startHeight;
            text.Width = startWidth;
            return false;
        }
    }

    /// <summary>
    /// The box the SOURCE text occupied, used as the interference baseline so an overlap the
    /// drawing already had is not blamed on the translation.
    /// </summary>
    private static Extents3d? BaselineOf(ReplacedEntityInfo item)
    {
        if (item.OriginalSnapshot == null) return null;
        try { return CollisionDetector.GetCorrectedBounds(item.OriginalSnapshot, false); }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a post-pass collision by scaling the translated text down until it no longer
    /// intersects its neighbours, searching for the LARGEST scale that is clear rather than
    /// stepping down until it happens to be clear. Height and wrap width are scaled together so
    /// the MText block scales uniformly: the glyphs-per-line count, and therefore the line count,
    /// stay stable and only the rendered size changes.
    /// </summary>
    /// <returns>True when the entity was separated (possibly needing no shrink at all).</returns>
    private static bool TryShrinkUntilClear(Entity entity, double originalHeight, Transaction tr, Extents3d? baseline, out int steps, string? unfittedContents = null)
    {
        // A local counter: an out parameter cannot be captured by the local functions below.
        int tries = 0;
        steps = 0;

        // A source label can already straddle a table border. Shrinking about its
        // existing attachment point never clears that border. Retry inside the strict
        // cell interior before shrinking in place; keep the change only after a full
        // collision audit. The normal source-footprint allowance remains unchanged.
        if (entity is MText cellText && baseline.HasValue && Math.Abs(Math.Sin(cellText.Rotation)) < .001)
        {
            using var saved = (MText)cellText.Clone();
            AvailableTextSpace.Refresh(tr);
            if (originalHeight > 0) cellText.TextHeight = originalHeight;
            var interior = AvailableTextSpace.Measure(cellText, baseline.Value, tr, false,
                out var cellLength, preserveSourceFootprint: false);
            cellText.TextHeight = saved.TextHeight;
            // Retry the original candidate, never compound the previous condensation.
            if (unfittedContents != null) cellText.Contents = unfittedContents;
            if (FitMTextToOriginalEnvelope(cellText, interior, originalHeight,
                    cellText.Handle.ToString(), false, cellLength))
            {
                AvailableTextSpace.Refresh(tr);
                if (!AvailableTextSpace.FindIntersections(cellText, tr, baseline).Any())
                {
                    steps = 1;
                    return true;
                }
            }
            cellText.CopyFrom(saved);
            cellText.RecordGraphicsModified(true);
            AvailableTextSpace.Refresh(tr);
        }
        double startHeight = entity switch
        {
            DBText dbText => dbText.Height,
            MText mtext => mtext.TextHeight,
            _ => 0
        };
        if (startHeight <= 0) return false;
        double startWidth = entity is MText startMText ? startMText.Width : 0;

        // Relative floor: never smaller than the absolute height floor the envelope fit respects.
        double floorScale = originalHeight > 0
            ? Math.Max(originalHeight * WritebackConstants.AbsoluteMinFitHeightRatio / startHeight, 0.05)
            : 0.05;

        void Apply(double scale)
        {
            switch (entity)
            {
                case DBText dbText:
                    dbText.Height = startHeight * scale;
                    break;
                case MText mtext:
                    mtext.TextHeight = startHeight * scale;
                    if (startWidth > 0) mtext.Width = startWidth * scale;
                    break;
            }
            entity.RecordGraphicsModified(true);
        }

        bool IsClear()
        {
            AvailableTextSpace.Refresh(tr);
            tries++;
            return !AvailableTextSpace.FindIntersections(entity, tr, baseline)
                .Any(issue => issue.StartsWith("CONFLICT=", StringComparison.Ordinal));
        }

        // Obstacle boxes are cached per BlockTableRecord and were built before this pass, so the
        // first test has to rebuild them. Another entity's shrink may already have cleared this
        // one, and the row-neighbour chains on a rotated drawing resolve from one end.
        if (IsClear()) { steps = 0; return true; }

        // IsClear is monotone in the scale (a smaller text overlaps no more than a larger one), so
        // bisect between the known-conflicting original size and the floor.
        Apply(floorScale);
        if (!IsClear()) { steps = tries; return false; }   // even the floor is not clear

        double clear = floorScale, conflicting = 1.0;
        for (int step = 0; step < 8; step++)
        {
            double mid = (clear + conflicting) / 2;
            Apply(mid);
            if (IsClear()) clear = mid; else conflicting = mid;
        }
        Apply(clear);
        bool separated = IsClear();
        steps = tries;
        return separated;
    }

    private static Extents3d AlignInsideEnvelope(Entity text, Extents3d current, Extents3d original)
    {
        // Font bearings and attachment points shift after font substitution.
        // Shrinking alone never fixes a min-edge offset; translate by only the
        // displacement needed to place a fitting box back inside the original.
        double dx = TextEnvelopeGeometry.FittingAxisOffset(current.MinPoint.X, current.MaxPoint.X,
            original.MinPoint.X, original.MaxPoint.X);
        double dy = TextEnvelopeGeometry.FittingAxisOffset(current.MinPoint.Y, current.MaxPoint.Y,
            original.MinPoint.Y, original.MaxPoint.Y);
        if (dx != 0 || dy != 0)
        {
            text.TransformBy(Matrix3d.Displacement(new Vector3d(dx, dy, 0)));
            text.RecordGraphicsModified(true);
            return CollisionDetector.GetCorrectedBounds(text);
        }
        return current;
    }

    private static bool IsInsideOriginalEnvelope(Extents3d current, Extents3d original, double originalHeight)
    {
        return TextEnvelopeGeometry.IsAxisInside(current.MinPoint.X, current.MaxPoint.X, original.MinPoint.X, original.MaxPoint.X)
            && TextEnvelopeGeometry.IsAxisInside(current.MinPoint.Y, current.MaxPoint.Y, original.MinPoint.Y, original.MaxPoint.Y);
    }

    private static double NormalizeHalfTurn(double rotation)
    {
        return TextEnvelopeGeometry.NormalizeHalfTurn(rotation);
    }

}
