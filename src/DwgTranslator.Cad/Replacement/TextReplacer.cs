using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Cad;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Replaces text in DWG entities with translated text.
/// Handles font mapping and auto-scaling for text overflow.
/// </summary>
public class TextReplacer
{
    private readonly double _autoScaleThreshold;
    private readonly double _autoScaleFactor;
    private readonly bool _cnToEn;

    public TextReplacer(double autoScaleThreshold = 1.5, double autoScaleFactor = 0.95, bool cnToEn = true)
    {
        _autoScaleThreshold = autoScaleThreshold;
        _autoScaleFactor = autoScaleFactor;
        _cnToEn = cnToEn;
    }

    public WritebackResult ReplaceAll(Database db, List<TextEntity> entities)
    {
        var result = new WritebackResult();

        var backupPath = CreateBackup(db);
        result.BackupPath = backupPath;

        using var transaction = db.TransactionManager.StartTransaction();
        try
        {
            var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var modelSpace = (BlockTableRecord)transaction.GetObject(modelSpaceId, OpenMode.ForRead);
            var blockTable = (BlockTable)transaction.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (var entity in entities)
            {
                if (entity.Status != TranslationStatus.Reviewed &&
                    entity.Status != TranslationStatus.Translated)
                {
                    result.SkippedCount++;
                    continue;
                }

                var replaceResult = ReplaceSingleEntity(transaction, db, entity);
                if (replaceResult.Success)
                {
                    result.SuccessCount++;
                    entity.Status = TranslationStatus.WritebackSuccess;
                }
                else
                {
                    result.FailCount++;
                    entity.Status = TranslationStatus.WritebackFailed;
                    result.Errors.Add($"Handle {entity.Handle}: {replaceResult.Error}");
                    Log.Warning("Failed to replace entity {Handle}: {Error}", entity.Handle, replaceResult.Error);
                }
            }

            transaction.Commit();
            Log.Information("Writeback complete: {Success} success, {Failed} failed, {Skipped} skipped",
                result.SuccessCount, result.FailCount, result.SkippedCount);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            Log.Error(ex, "Writeback transaction failed, rolling back");
            transaction.Abort();
            result.Errors.Add($"Transaction failed: {ex.Message}");
            RestoreBackup(db, backupPath);
        }

        return result;
    }

    private EntityReplaceResult ReplaceSingleEntity(Transaction tr, Database db, TextEntity entity)
    {
        try
        {
            if (entity.Handle.Contains('/'))
                return ReplaceAttribute(tr, db, entity);
            if (entity.Handle.Contains(':'))
                return ReplaceTableCell(tr, db, entity);

            var handle = new Handle(Convert.ToInt64(entity.Handle, 16));
            var objectId = db.GetObjectId(false, handle, 0);
            var dbObject = tr.GetObject(objectId, OpenMode.ForWrite);

            BlockTableRecord? owningBtr = null;
            try
            {
                if (dbObject.OwnerId.IsValid)
                    owningBtr = tr.GetObject(dbObject.OwnerId, OpenMode.ForRead) as BlockTableRecord;
            }
            catch { }

            switch (dbObject)
            {
                case DBText dbText:
                    return ReplaceDBText(dbText, entity, db, owningBtr);
                case MText mText:
                    return ReplaceMText(mText, entity, db, owningBtr);
                case Dimension dim:
                    return ReplaceDimension(dim, entity, owningBtr);
                case MLeader mLeader:
                    return ReplaceMLeader(mLeader, entity, db, owningBtr);
                default:
                    return new EntityReplaceResult { Success = false, Error = "Unsupported entity type" };
            }
        }
        catch (Exception ex)
        {
            return new EntityReplaceResult { Success = false, Error = ex.Message };
        }
    }

    private EntityReplaceResult ReplaceDBText(DBText dbText, TextEntity entity, Database db, BlockTableRecord? owningBtr)
    {
        var originalHeight = dbText.Height;
        dbText.TextString = entity.TranslatedText;
        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = dbText, OwningBlock = owningBtr,
            OriginalHeight = originalHeight, CurrentHeight = dbText.Height
        };
    }

    private EntityReplaceResult ReplaceMText(MText mText, TextEntity entity, Database db, BlockTableRecord? owningBtr)
    {
        var originalHeight = mText.TextHeight;

        if (entity.MTextLineSpacing > 0)
            mText.LineSpacingFactor = entity.MTextLineSpacing;
        if (entity.MTextLineSpacingStyle > 0)
            mText.LineSpacingStyle = (LineSpacingStyle)entity.MTextLineSpacingStyle;

        mText.Contents = entity.TranslatedText
            .Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");

        MapTextStyle(mText.TextStyleId, db, _cnToEn);
        mText.RecordGraphicsModified(true);

        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = mText, OwningBlock = owningBtr,
            OriginalHeight = originalHeight, CurrentHeight = mText.TextHeight
        };
    }

    private static EntityReplaceResult ReplaceDimension(Dimension dim, TextEntity entity, BlockTableRecord? owningBtr)
    {
        dim.DimensionText = entity.TranslatedText;
        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = dim, OwningBlock = owningBtr, OriginalHeight = 2.5
        };
    }

    private EntityReplaceResult ReplaceMLeader(MLeader mLeader, TextEntity entity, Database db, BlockTableRecord? owningBtr)
    {
        var originalHeight = mLeader.MText?.TextHeight ?? 2.5;
        if (mLeader.MText != null)
        {
            mLeader.MText.Contents = entity.TranslatedText;
            MapTextStyle(mLeader.MText.TextStyleId, db, _cnToEn);
        }
        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = mLeader.MText, OwningBlock = owningBtr, OriginalHeight = originalHeight
        };
    }

    private EntityReplaceResult ReplaceAttribute(Transaction tr, Database db, TextEntity entity)
    {
        var parts = entity.Handle.Split('/');
        if (parts.Length != 2)
            return new EntityReplaceResult { Success = false, Error = "Invalid attribute handle format" };

        var blockRefHandle = new Handle(Convert.ToInt64(parts[0], 16));
        var attTag = parts[1];

        var objectId = db.GetObjectId(false, blockRefHandle, 0);
        var blockRef = (BlockReference)tr.GetObject(objectId, OpenMode.ForWrite);

        BlockTableRecord? owningBtr = null;
        try
        {
            if (blockRef.OwnerId.IsValid)
                owningBtr = tr.GetObject(blockRef.OwnerId, OpenMode.ForRead) as BlockTableRecord;
        }
        catch { }

        foreach (ObjectId attId in blockRef.AttributeCollection)
        {
            var attRef = (AttributeReference)tr.GetObject(attId, OpenMode.ForWrite);
            if (attRef.Tag == attTag)
            {
                var originalHeight = attRef.Height;
                attRef.TextString = entity.TranslatedText;
                blockRef.RecordGraphicsModified(true);
                return new EntityReplaceResult
                {
                    Success = true, ModifiedEntity = attRef, OwningBlock = owningBtr, OriginalHeight = originalHeight
                };
            }
        }

        return new EntityReplaceResult { Success = false, Error = $"Attribute '{attTag}' not found" };
    }

    private EntityReplaceResult ReplaceTableCell(Transaction tr, Database db, TextEntity entity)
    {
        var parts = entity.Handle.Split(':');
        if (parts.Length != 3)
            return new EntityReplaceResult { Success = false, Error = "Invalid table cell handle format" };

        var tableHandle = new Handle(Convert.ToInt64(parts[0], 16));
        var row = int.Parse(parts[1]);
        var col = int.Parse(parts[2]);

        var objectId = db.GetObjectId(false, tableHandle, 0);
        var table = (Table)tr.GetObject(objectId, OpenMode.ForWrite);

        BlockTableRecord? owningBtr = null;
        try
        {
            if (table.OwnerId.IsValid)
                owningBtr = tr.GetObject(table.OwnerId, OpenMode.ForRead) as BlockTableRecord;
        }
        catch { }

        if (row < table.Rows.Count && col < table.Columns.Count)
        {
            var cell = table.Cells[row, col];
            cell.Value = entity.TranslatedText;
            return new EntityReplaceResult
            {
                Success = true, ModifiedEntity = null, OwningBlock = owningBtr, OriginalHeight = 2.5
            };
        }

        return new EntityReplaceResult { Success = false, Error = "Table cell out of range" };
    }

    private void MapTextStyle(ObjectId styleId, Database db, bool cnToEn = true)
    {
        if (!styleId.IsValid) return;

        try
        {
            var style = (TextStyleTableRecord)styleId.GetObject(OpenMode.ForWrite);

            var mappedFontName = FontMapper.MapFontName(style.Name, cnToEn);
            if (string.IsNullOrEmpty(mappedFontName)) return;

            bool isShx = mappedFontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

            if (isShx)
            {
                style.FileName = mappedFontName;
                style.BigFontFileName = string.Empty;
            }
            else
            {
                var currentFont = style.Font;
                var newFont = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(mappedFontName, currentFont.Bold, currentFont.Italic, 0, 0);
                style.Font = newFont;
            }
            Log.Information("Font mapped: {Style} -> {Font}", style.Name, mappedFontName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for style {Id}", styleId.Handle);
        }
    }

    private static string CreateBackup(Database db)
    {
        var originalPath = db.Filename;
        var backupPath = Path.ChangeExtension(originalPath, ".bak");

        try
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Copy(originalPath, backupPath);
            Log.Information("Backup created: {Path}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create backup");
            backupPath = string.Empty;
        }

        return backupPath;
    }

    private static void RestoreBackup(Database db, string backupPath)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return;

        try
        {
            var originalPath = db.Filename;
            var restorePath = Path.ChangeExtension(originalPath, ".restored.dwg");
            File.Copy(backupPath, restorePath, overwrite: true);
            Log.Warning("Backup restored to {Path} (original file is locked by AutoCAD)", restorePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore backup");
        }
    }
}
