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

    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool cnToEn = true)
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
                db.ReadDwgFile(sourceFilePath, FileOpenMode.OpenForReadAndAllShare, false, null);
                WriteTranslationsToDatabase(db, entities, cnToEn, result);

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

                if (File.Exists(outputFilePath)) File.Delete(outputFilePath);
                File.Move(tempOutputPath, outputFilePath);
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
                catch (Exception cleanupEx) { Log.Debug("AcadWriterEngine", "Failed to remove temporary DWG {0}: {1}", tempOutputPath, cleanupEx.Message); }
            }
        }

        return result;
    }

    public CadWriteResult WriteTranslations(Database db, List<TextEntity> entities, bool cnToEn = true)
    {
        var result = new CadWriteResult();
        try
        {
            WriteTranslationsToDatabase(db, entities, cnToEn, result);
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

    private void WriteTranslationsToDatabase(Database db, List<TextEntity> entities, bool cnToEn, CadWriteResult result)
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

            // Model space
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForWrite);
            var errors = new List<string>();
            successCount += ProcessBlockTableRecord(modelSpace, tr, entityMap, unprocessed, cnToEn, errors, layoutRejected, replacedEntities);
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
                        successCount += ProcessBlockTableRecord(btr, tr, entityMap, unprocessed, cnToEn, errors, layoutRejected, replacedEntities);
                    }
                }
            }

            if (unprocessed.Count > 0)
                Log.Warning("AcadWriter: skipped {Count} unmatched entities", unprocessed.Count);

            // Rebuild from the completed layout, not the original snapshot.
            // This catches two translations growing into the same previously empty gap.
            AvailableTextSpace.Refresh(tr);
            var conflicted = replacedEntities
                .Select(item => new
                {
                    Item = item,
                    Issues = AvailableTextSpace.FindIntersections(
                            (Entity)tr.GetObject(item.EntityId, OpenMode.ForRead), tr)
                        .Where(issue => issue.StartsWith("CONFLICT=", StringComparison.Ordinal))
                        .ToList()
                })
                .Where(x => x.Issues.Count > 0)
                .ToList();
            var unrecoverableConflicts = new List<string>();
            foreach (var conflict in conflicted)
            {
                var handle = conflict.Item.EntityId.Handle.ToString();
                if (conflict.Item.OriginalSnapshot == null)
                {
                    unrecoverableConflicts.AddRange(conflict.Issues);
                    continue;
                }

                var current = (Entity)tr.GetObject(conflict.Item.EntityId, OpenMode.ForWrite);
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
            // Unmatched or failed replacements must roll back the transaction:
            // otherwise a partially mutated entity could be saved as a success.
            if (unprocessed.Count > 0 || errors.Count > 0)
                throw new InvalidOperationException("Writeback incomplete for handles: " +
                    string.Join(", ", unprocessed.Take(20)) + "; " + string.Join("; ", errors.Take(5)));

            if (layoutRejected.Count > 0)
            {
                var detail = "Original text preserved after layout rejection for handles: " +
                    string.Join(", ", layoutRejected.Take(20));
                result.Errors.Add(detail);
                Log.Warning("{Detail}", detail);
            }

            Log.Information("Measured envelope verification passed for {Count} replacements", replacedEntities.Count);

            tr.Commit();
        }
    }

    private int ProcessBlockTableRecord(
        BlockTableRecord btr, Transaction tr,
        Dictionary<string, TextEntity> entityMap, HashSet<string> unprocessed,
        bool cnToEn, List<string> errors, HashSet<string> layoutRejected,
        List<ReplacedEntityInfo> replacedEntities)
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
                bool wasAlreadyApplied = entity switch
                {
                    DBText d => string.Equals(d.TextString, textEntity.TranslatedText, StringComparison.Ordinal),
                    MText m => string.Equals(m.Contents, textEntity.TranslatedText, StringComparison.Ordinal),
                    Dimension dimension => string.Equals(dimension.DimensionText, textEntity.TranslatedText, StringComparison.Ordinal),
                    _ => false
                };
                Entity? originalSnapshot = wasAlreadyApplied ? null : entity.Clone() as Entity;
                entity.UpgradeOpen();
                if (ReplaceEntity(entity, textEntity, cnToEn, tr, out var envelopeFitSucceeded))
                {
                    if (!envelopeFitSucceeded)
                    {
                        Log.Warning("Layout fit rejected {Handle}: {Text}; height={Height}; width={Width}", handleStr,
                            textEntity.PlainText, entity is DBText d ? d.Height : entity is MText m ? m.TextHeight : 0,
                            entity is DBText d2 ? d2.WidthFactor : entity is MText m2 ? m2.Width : 0);
                        layoutRejected.Add(handleStr);
                        unprocessed.Remove(handleStr);
                        originalSnapshot?.Dispose();
                        continue;
                    }
                    count++;
                    unprocessed.Remove(handleStr);
                    if (!wasAlreadyApplied)
                    {
                        replacedEntities.Add(new ReplacedEntityInfo
                        {
                            EntityId = entity.ObjectId,
                            OriginalHeight = textEntity.OriginalHeight > 0 ? textEntity.OriginalHeight : textEntity.Height,
                            OwningBtr = btr,
                            EnvelopeFitSucceeded = envelopeFitSucceeded,
                            OriginalSnapshot = originalSnapshot
                        });
                    }
                }
                else
                {
                    originalSnapshot?.Dispose();
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
                        if (AttributeTranslationPolicy.IsMetadataTag(att.Tag))
                        {
                            // Accept legacy requests without mutating machine mappings
                            // or asking invisible attributes for geometric extents.
                            unprocessed.Remove(compoundHandle);
                            count++;
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
                            count++;
                            unprocessed.Remove(compoundHandle);
                            if (!unchanged)
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
                            Log.Warning(ex, "Failed to update attribute {Tag}", att.Tag);
                        }
                    }
                }
            }

            if (entity is Table table)
            {
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
                Log.Warning(ex, "Failed to update table cell {Key}", key);
                errors.Add($"Table cell {key}: {ex.Message}");
            }
        }

        return count;
    }

    private static bool ReplaceEntity(
        Entity entity, TextEntity ourEntity, bool cnToEn, Transaction tr,
        out bool envelopeFitSucceeded)
    {
        envelopeFitSucceeded = true;
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;
            if (entity is AttributeDefinition definition && !definition.Constant)
            {
                Log.Information("Preserved non-rendered attribute template {Handle}", ourEntity.Handle);
                return true;
            }
            bool characterColumn = cnToEn && entity is MText sourceMText &&
                DwgTranslator.Core.Services.VerticalTextLayout.IsCharacterColumn(
                    ourEntity.PlainText, sourceMText.Rotation, sourceMText.Width, sourceMText.TextHeight);
            Extents3d? originalBounds = TryGetEntityBounds(entity);
            if (originalBounds.HasValue)
                originalBounds = AvailableTextSpace.Measure(entity, originalBounds.Value, tr, characterColumn);

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
                    AcadFontApplier.MapFont(dbText, ourEntity.TextStyleName, cnToEn, tr);
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

                    mtext.Contents = DwgTranslator.Core.Services.FontMapper.MapInlineFonts(translatedText, cnToEn)
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");

                    mtext.ColumnType = ColumnType.NoColumns;
                    if (characterColumn)
                    {
                        // Preserve the column's world-space corridor, but read English
                        // bottom-to-top as a coherent phrase (the user's 90-degree example).
                        mtext.Rotation = Math.PI / 2;
                        mtext.Contents = System.Text.RegularExpressions.Regex.Replace(
                            mtext.Contents.Replace("\\P", " "), @"\s+", " ").Trim();
                    }

                    // Keep a non-zero wrap width when the source had a fixed rectangle.
                    // Width=0 disables AutoCAD word wrap and makes long EN text collide.
                    if (ourEntity.MTextRectangleWidth > 0)
                        mtext.Width = ourEntity.MTextRectangleWidth;

                    // Restore original text height
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    AcadFontApplier.MapFont(mtext, ourEntity.TextStyleName, cnToEn, tr);
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    // Fit against measured free space before reducing height.
                    // The legacy estimator reduced height before trying that width.
                    envelopeFitSucceeded = FitMTextToOriginalEnvelope(
                        mtext, originalBounds,
                        ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height,
                        ourEntity.Handle, characterColumn);
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
                    AcadFontApplier.MapFont(dim, ourEntity.TextStyleName, cnToEn, tr);
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
                        AcadFontApplier.MapFont(leaderText, ourEntity.TextStyleName, cnToEn, tr);
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

    private static bool FitDbTextToOriginalEnvelope(
        DBText text, Extents3d? originalBounds, double originalHeight, string handle)
    {
        if (!originalBounds.HasValue) return false;
        double startWidthFactor = text.WidthFactor;
        double startHeight = text.Height;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            text.RecordGraphicsModified(true);
            Extents3d current;
            try { current = CollisionDetector.GetCorrectedBounds(text); }
            catch { return false; }

            current = AlignInsideEnvelope(text, current, originalBounds.Value);

            if (IsInsideOriginalEnvelope(current, originalBounds.Value, originalHeight))
            {
                if (text.Height < startHeight*.70-1e-8 || text.WidthFactor < Math.Min(startWidthFactor,.40)-1e-8)
                    return false;
                if (attempt > 0)
                    Log.Information(
                        "Envelope fit DBText {Handle}: widthFactor {OldW:F3}->{NewW:F3}, height {OldH:F3}->{NewH:F3}",
                        handle, startWidthFactor, text.WidthFactor, startHeight, text.Height);
                return true;
            }

            double rotation = NormalizeHalfTurn(text.Rotation);
            bool vertical = Math.Abs(Math.Sin(rotation)) > Math.Abs(Math.Cos(rotation));
            double originalLength = vertical
                ? originalBounds.Value.MaxPoint.Y - originalBounds.Value.MinPoint.Y
                : originalBounds.Value.MaxPoint.X - originalBounds.Value.MinPoint.X;
            double currentLength = vertical
                ? current.MaxPoint.Y - current.MinPoint.Y
                : current.MaxPoint.X - current.MinPoint.X;

            if (currentLength > originalLength && currentLength > 0)
            {
                double ratio = DwgTranslator.Cad.Compat.Clamp(
                    originalLength / currentLength * 0.97, 0.08, 0.98);
                double minimumWidth = Math.Min(startWidthFactor, .40);
                double desiredWidth = text.WidthFactor * ratio;
                if (desiredWidth < minimumWidth)
                {
                    text.Height *= desiredWidth / minimumWidth;
                    text.WidthFactor = minimumWidth;
                }
                else text.WidthFactor = Math.Min(desiredWidth, 100.0);
            }
            else
            {
                double originalW = Math.Max(originalBounds.Value.MaxPoint.X - originalBounds.Value.MinPoint.X, 0.001);
                double originalH = Math.Max(originalBounds.Value.MaxPoint.Y - originalBounds.Value.MinPoint.Y, 0.001);
                double currentW = Math.Max(current.MaxPoint.X - current.MinPoint.X, 0.001);
                double currentH = Math.Max(current.MaxPoint.Y - current.MinPoint.Y, 0.001);
                double ratio = Math.Min(originalW / currentW, originalH / currentH) * 0.95;
                text.Height *= DwgTranslator.Cad.Compat.Clamp(ratio, 0.20, 0.98);
            }
        }

        try
        {
            var finalBounds = AlignInsideEnvelope(text,CollisionDetector.GetCorrectedBounds(text),originalBounds.Value);
            bool fitted = IsInsideOriginalEnvelope(finalBounds, originalBounds.Value, originalHeight);
            if (!fitted) Log.Warning("DBText final bounds {Handle}: allowed={Allowed}; ink={Ink}",handle,originalBounds.Value,finalBounds);
            if (!fitted)
                Log.Warning("Envelope fit DBText {Handle}: precise fallback required", handle);
            return fitted && text.Height >= startHeight*.70-1e-8 && text.WidthFactor >= Math.Min(startWidthFactor,.40)-1e-8;
        }
        catch { return false; }
    }

    private static bool FitMTextToOriginalEnvelope(
        MText text, Extents3d? originalBounds, double originalHeight, string handle, bool singleLine = false)
    {
        var contents=text.Contents;
        var location=text.Location;
        var height=text.TextHeight;
        var width=text.Width;
        foreach(double factor in new[]{1.0,.9,.8,.75})
        {
            text.Location=location; text.TextHeight=height; text.Width=width;
            text.Contents=factor==1 ? contents : "{\\W"+factor.ToString(System.Globalization.CultureInfo.InvariantCulture)+";"+contents+"}";
            if(FitMTextCandidate(text,originalBounds,originalHeight,handle,singleLine))return true;
        }
        text.Location=location; text.TextHeight=height; text.Width=width; text.Contents=contents;
        return false;
    }

    private static bool FitMTextCandidate(
        MText text, Extents3d? originalBounds, double originalHeight, string handle, bool singleLine)
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
            double high = originalHeight > 0 ? originalHeight : startHeight;
            double low = high*.70;
            double best = 0;
            for (int step=0; step<14; step++)
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
                return IsInsideOriginalEnvelope(bounds,box,originalHeight);
            }
            text.TextHeight=startHeight;
            text.Width=startWidth;
            return false;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            text.RecordGraphicsModified(true);
            Extents3d current;
            try { current = CollisionDetector.GetCorrectedBounds(text); }
            catch { return false; }

            current = AlignInsideEnvelope(text, current, originalBounds.Value);

            if (IsInsideOriginalEnvelope(current, originalBounds.Value, originalHeight))
            {
                if (text.TextHeight < originalHeight*.70) return false;
                if (attempt > 0)
                    Log.Information(
                        "Envelope fit MText {Handle}: width {OldW:F3}->{NewW:F3}, height {OldH:F3}->{NewH:F3}",
                        handle, startWidth, text.Width, startHeight, text.TextHeight);
                return true;
            }

            double originalW = Math.Max(originalBounds.Value.MaxPoint.X - originalBounds.Value.MinPoint.X, 0.001);
            double originalH = Math.Max(originalBounds.Value.MaxPoint.Y - originalBounds.Value.MinPoint.Y, 0.001);
            double currentW = Math.Max(current.MaxPoint.X - current.MinPoint.X, 0.001);
            double currentH = Math.Max(current.MaxPoint.Y - current.MinPoint.Y, 0.001);
            double rotation = NormalizeHalfTurn(text.Rotation);
            bool vertical = Math.Abs(Math.Sin(rotation)) > Math.Abs(Math.Cos(rotation));
            double originalLength = vertical ? originalH : originalW;

            if (text.Width <= 0 || text.Width > originalLength * 0.96)
                text.Width = Math.Max(originalLength * 0.96, 0.1);

            double ratio = Math.Min(originalW / currentW, originalH / currentH) * 0.95;
            text.TextHeight *= DwgTranslator.Cad.Compat.Clamp(ratio, 0.20, 0.98);
        }

        try
        {
            bool fitted = IsInsideOriginalEnvelope(
                CollisionDetector.GetCorrectedBounds(text), originalBounds.Value, originalHeight);
            if (!fitted)
                Log.Warning("Envelope fit MText {Handle}: precise fallback required", handle);
            return fitted && text.TextHeight >= originalHeight*.70;
        }
        catch { return false; }
    }

    private static Extents3d AlignInsideEnvelope(Entity text, Extents3d current, Extents3d original)
    {
        // Font bearings and attachment points shift after font substitution.
        // Shrinking alone never fixes a min-edge offset; translate by only the
        // displacement needed to place a fitting box back inside the original.
        double dx = 0, dy = 0;
        if (current.MaxPoint.X - current.MinPoint.X <= original.MaxPoint.X - original.MinPoint.X)
            dx = current.MinPoint.X < original.MinPoint.X ? original.MinPoint.X - current.MinPoint.X
                : current.MaxPoint.X > original.MaxPoint.X ? original.MaxPoint.X - current.MaxPoint.X : 0;
        if (current.MaxPoint.Y - current.MinPoint.Y <= original.MaxPoint.Y - original.MinPoint.Y)
            dy = current.MinPoint.Y < original.MinPoint.Y ? original.MinPoint.Y - current.MinPoint.Y
                : current.MaxPoint.Y > original.MaxPoint.Y ? original.MaxPoint.Y - current.MaxPoint.Y : 0;
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
        const double tolerance = 0.01;
        return current.MinPoint.X >= original.MinPoint.X - tolerance
            && current.MaxPoint.X <= original.MaxPoint.X + tolerance
            && current.MinPoint.Y >= original.MinPoint.Y - tolerance
            && current.MaxPoint.Y <= original.MaxPoint.Y + tolerance;
    }

    private static double NormalizeHalfTurn(double rotation)
    {
        double value = rotation % Math.PI;
        return value < 0 ? value + Math.PI : value;
    }

}
