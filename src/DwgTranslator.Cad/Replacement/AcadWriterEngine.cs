using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Core.Models;
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
                    AcadFontApplier.MapFont(dbText, ourEntity.TextStyleName, cnToEn, tr);
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

                    AcadFontApplier.MapFont(mtext, ourEntity.TextStyleName, cnToEn, tr);
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
}
