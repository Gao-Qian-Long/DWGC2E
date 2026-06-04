using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Cad;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// High-precision DWG writeback engine using AutoCAD .NET API.
/// 2-phase pipeline: WRITE (set text+font) → COMMIT (save).
/// </summary>
public class AcadWriterEngine
{
    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<TextEntity> entities, bool cnToEn = true)
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
            result.Errors.Add($"Writeback error: {ex.Message}");
        }
        return result;
    }

    private void WriteTranslationsToDatabase(Database db, List<TextEntity> entities, bool cnToEn, CadWriteResult result)
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var entityMap = entities
                .Where(e => e.Status == TranslationStatus.Translated ||
                            e.Status == TranslationStatus.Reviewed)
                .ToDictionary(e => e.Handle, StringComparer.OrdinalIgnoreCase);

            int successCount = 0;
            var unprocessed = new HashSet<string>(entityMap.Keys, StringComparer.OrdinalIgnoreCase);

            // Model space
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var modelSpace = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForWrite);
            var errors = new List<string>();
            successCount += ProcessBlockTableRecord(modelSpace, tr, entityMap, unprocessed, cnToEn, errors);
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
                        successCount += ProcessBlockTableRecord(btr, tr, entityMap, unprocessed, cnToEn, errors);
                    }
                }
            }

            result.SuccessCount = successCount;
            result.FailCount = unprocessed.Count;

            if (unprocessed.Count > 0)
                Log.Warning("AcadWriter: skipped {Count} unmatched entities", unprocessed.Count);

            tr.Commit();
        }
    }

    private int ProcessBlockTableRecord(
        BlockTableRecord btr, Transaction tr,
        Dictionary<string, TextEntity> entityMap, HashSet<string> unprocessed,
        bool cnToEn, List<string> errors)
    {
        int count = 0;
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
                entity.UpgradeOpen();
                if (ReplaceEntity(entity, textEntity, cnToEn, tr))
                {
                    count++;
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
                        try
                        {
                            att.TextString = attEntity.TranslatedText;
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

    private static bool ReplaceEntity(Entity entity, TextEntity ourEntity, bool cnToEn, Transaction tr)
    {
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;

            switch (entity)
            {
                case DBText dbText:
                    dbText.TextString = translatedText;
                    // Restore original text height before font mapping.
                    // Font mapping changes the TextStyleId; if the new text style
                    // has a fixed height, it would override the entity height.
                    // We explicitly reset it to the extraction-time value first.
                    if (ourEntity.Height > 0)
                        dbText.Height = ourEntity.Height;
                    AcadFontApplier.MapFont(dbText, ourEntity.TextStyleName, cnToEn, tr);
                    // Re-apply height after font mapping in case the new style
                    // has a fixed height that overrode our setting.
                    if (ourEntity.Height > 0)
                        dbText.Height = ourEntity.Height;
                    // Apply width-based height scaling (same logic as offline DwgTextScaler)
                    ApplyHeightScaling(dbText, translatedText, ourEntity);
                    dbText.RecordGraphicsModified(true);
                    return true;

                case MText mtext:
                    if (ourEntity.MTextLineSpacing > 0)
                        mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
                    if (ourEntity.MTextLineSpacingStyle > 0)
                        mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

                    mtext.Contents = translatedText
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");

                    // Restore original text height and apply scaling
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    AcadFontApplier.MapFont(mtext, ourEntity.TextStyleName, cnToEn, tr);
                    if (ourEntity.Height > 0)
                        mtext.TextHeight = ourEntity.Height;
                    ApplyHeightScaling(mtext, translatedText, ourEntity);
                    mtext.RecordGraphicsModified(true);
                    return true;

                case Dimension dim:
                    AcadFontApplier.MapFont(dim, ourEntity.TextStyleName, cnToEn, tr);
                    dim.DimensionText = translatedText;
                    return true;

                case MLeader mLeader:
                    if (mLeader.MText != null)
                    {
                        mLeader.MText.Contents = translatedText
                            .Replace("\r\n", "\\P")
                            .Replace("\n", "\\P")
                            .Replace("\r", "\\P");
                        AcadFontApplier.MapFont(mLeader.MText, ourEntity.TextStyleName, cnToEn, tr);
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
    /// Width-based height scaling for DBText (AutoCAD .NET API).
    /// Ports the same logic from DwgTextScaler.ApplyScaling for offline writeback.
    /// Reduces text height proportionally when translated text is wider than original.
    /// </summary>
    private static void ApplyHeightScaling(DBText text, string translatedText, TextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        try
        {
            double currentHeight = text.Height > 0 ? text.Height : originalHeight;
            double newWidth = TextWidthEstimator.EstimateTextWidth(translatedText, currentHeight);

            if (newWidth > originalWidth)
            {
                double scale = originalWidth / newWidth;
                if (scale < 0.85) scale = 0.85;
                double newHeight = originalHeight * scale;
                text.Height = newHeight;
            }

            // Conservative cap: never grow beyond original height + 5%
            if (originalHeight > 0 && text.Height > originalHeight * 1.05)
                text.Height = originalHeight * 1.05;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Height scaling failed for DBText {Handle}", text.Handle);
        }
    }

    /// <summary>
    /// Width-based height scaling for MText (AutoCAD .NET API).
    /// Ports the same logic from DwgTextScaler.ApplyScaling for offline writeback.
    /// Adjusts TextHeight and optionally sets ColumnWidth for overflow control.
    /// </summary>
    private static void ApplyHeightScaling(MText mtext, string translatedText, TextEntity ourEntity)
    {
        double originalWidth = ourEntity.OriginalWidth;
        double originalHeight = ourEntity.OriginalHeight > 0 ? ourEntity.OriginalHeight : ourEntity.Height;
        if (originalWidth <= 0 || string.IsNullOrEmpty(translatedText)) return;

        try
        {
            double currentHeight = mtext.TextHeight > 0 ? mtext.TextHeight : originalHeight;
            double lineSpacing = ourEntity.MTextLineSpacing > 0 ? ourEntity.MTextLineSpacing : 1.0;

            // Split into logical lines for per-line width estimation
            var logicalLines = translatedText.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P")
                .Split(["\\P"], StringSplitOptions.None).ToList();
            double maxLineWidth = 0;
            foreach (var line in logicalLines)
            {
                double lineW = TextWidthEstimator.EstimateTextWidth(line, currentHeight);
                if (lineW > maxLineWidth) maxLineWidth = lineW;
            }

            // Also compute concatenated width
            string flatText = translatedText.Replace("\\P", " ").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            double concatenatedWidth = TextWidthEstimator.EstimateTextWidth(flatText, currentHeight);

            int lineCount = Math.Max(1, logicalLines.Count);
            int originalLineCount = ourEntity.MTextLineCount > 0 ? ourEntity.MTextLineCount : 1;
            double effectiveWidth = lineCount > 1 ? maxLineWidth : concatenatedWidth;

            // Check if height scaling is needed (text wider than original bounds)
            if (effectiveWidth > originalWidth)
            {
                double columnWidth = mtext.ColumnWidth;
                double effectiveRectWidth = columnWidth > 0 ? columnWidth : originalWidth;

                int estimatedLines = 0;
                foreach (var line in logicalLines)
                {
                    double lineW = TextWidthEstimator.EstimateTextWidth(line, currentHeight);
                    estimatedLines += Math.Max(1, (int)Math.Ceiling(lineW / (effectiveRectWidth * 1.05)));
                }
                estimatedLines = Math.Max(1, estimatedLines);

                if (estimatedLines > originalLineCount)
                {
                    double originalTotalHeight = originalLineCount * originalHeight * lineSpacing;
                    double translatedTotalHeight = estimatedLines * currentHeight * lineSpacing;

                    if (translatedTotalHeight > originalTotalHeight && originalTotalHeight > 0)
                    {
                        double scale = originalTotalHeight / translatedTotalHeight;
                        double newHeight = currentHeight * scale;
                        double minHeight = originalHeight * 0.85;
                        if (newHeight < minHeight) newHeight = minHeight;
                        if (newHeight < currentHeight)
                            mtext.TextHeight = newHeight;
                    }
                }
            }

            // Conservative cap
            if (originalHeight > 0 && mtext.TextHeight > originalHeight * 1.05)
                mtext.TextHeight = originalHeight * 1.05;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Height scaling failed for MText {Handle}", mtext.Handle);
        }
    }
}
