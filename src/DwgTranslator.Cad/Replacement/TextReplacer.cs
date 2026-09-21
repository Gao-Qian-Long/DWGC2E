#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
using DwgTranslator.Cad;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
#if GSTARCAD
using Gssoft.Gscad.GraphicsInterface;
using CadRuntimeException = Gssoft.Gscad.Runtime.Exception;
#else
using Autodesk.AutoCAD.GraphicsInterface;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
#endif

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Replaces text in DWG entities with translated text.
/// Handles font mapping and backup management.
/// </summary>
public class TextReplacer
{
    private const double DefaultTextHeight = 2.5;

    private readonly bool _targetIsCjk;

    public TextReplacer(bool targetIsCjk = true)
    {
        _targetIsCjk = targetIsCjk;
    }

    public WritebackResult ReplaceAll(Database db, List<TextEntity> entities)
    {
        var result = new WritebackResult();
        var backupPath = CreateBackup(db);
        result.BackupPath = backupPath;

        using var transaction = db.TransactionManager.StartTransaction();
        try
        {
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
                }
            }

            transaction.Commit();

            // Clean up backup on success
            if (result.FailCount == 0 && !string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
            {
                try { File.Delete(backupPath); } catch { /* best-effort */ }
            }
        }
        catch (CadRuntimeException ex)
        {
            Log.Error(ex, "Writeback transaction failed, rolling back");
            transaction.Abort();
            result.Errors.Add($"Transaction failed: {ex.Message}");
            RestoreBackup(db, backupPath);
        }
        catch (Exception ex)
        {
            // Unexpected failure (IO, mapping, host API): never leave the drawing
            // half-written or the transaction dangling. Abort, record, restore.
            Log.Error(ex, "Writeback failed with unexpected error, rolling back");
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
            var owningBtr = ResolveOwnerBtr(tr, dbObject.OwnerId);

            return dbObject switch
            {
                DBText dbText => ReplaceDBText(dbText, entity, db, tr, owningBtr),
                MText mText => ReplaceMText(mText, entity, db, tr, owningBtr),
                Dimension dim => ReplaceDimension(dim, entity, owningBtr),
                MLeader mLeader => ReplaceMLeader(mLeader, entity, db, tr, owningBtr),
                _ => new EntityReplaceResult { Success = false, Error = "Unsupported entity type" }
            };
        }
        catch (Exception ex)
        {
            return new EntityReplaceResult { Success = false, Error = ex.Message };
        }
    }

    private EntityReplaceResult ReplaceDBText(DBText dbText, TextEntity entity, Database db, Transaction tr, BlockTableRecord? owningBtr)
    {
        var originalHeight = dbText.Height;
        dbText.TextString = entity.TranslatedText;
        AssignMappedTextStyle(dbText, entity.TextStyleName, db, tr);
        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = dbText, OwningBlock = owningBtr,
            OriginalHeight = originalHeight, CurrentHeight = dbText.Height
        };
    }

    private EntityReplaceResult ReplaceMText(MText mText, TextEntity entity, Database db, Transaction tr, BlockTableRecord? owningBtr)
    {
        var originalHeight = mText.TextHeight;

        if (entity.MTextLineSpacing > 0)
            mText.LineSpacingFactor = entity.MTextLineSpacing;
        if (entity.MTextLineSpacingStyle > 0)
            mText.LineSpacingStyle = (LineSpacingStyle)entity.MTextLineSpacingStyle;

        mText.Contents = FontMapper.MapInlineFonts(entity.TranslatedText, _targetIsCjk)
            .Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");

        mText.ColumnType = ColumnType.NoColumns;

        AssignMappedTextStyle(mText, entity.TextStyleName, db, tr);
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
            Success = true, ModifiedEntity = dim, OwningBlock = owningBtr, OriginalHeight = DefaultTextHeight
        };
    }

    private EntityReplaceResult ReplaceMLeader(MLeader mLeader, TextEntity entity, Database db, Transaction tr, BlockTableRecord? owningBtr)
    {
        // MText is a detached copy in the host API. Mutate one copy, assign it
        // back to the leader, then dispose it — mutating the getter is a no-op.
        using var leaderText = mLeader.MText;
        if (leaderText == null)
            return new EntityReplaceResult { Success = false, Error = "MLeader has no MText content", OwningBlock = owningBtr };

        var originalHeight = leaderText.TextHeight;
        leaderText.Contents = FontMapper.MapInlineFonts(entity.TranslatedText, _targetIsCjk)
            .Replace("\r\n", "\\P")
            .Replace("\n", "\\P")
            .Replace("\r", "\\P");
        AssignMappedTextStyle(leaderText, entity.TextStyleName, db, tr);
        mLeader.MText = leaderText;

        return new EntityReplaceResult
        {
            Success = true, ModifiedEntity = mLeader, OwningBlock = owningBtr, OriginalHeight = originalHeight
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
        var owningBtr = ResolveOwnerBtr(tr, blockRef.OwnerId);

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
        if (row < 0 || col < 0)
            return new EntityReplaceResult { Success = false, Error = $"Invalid table cell index (row={row}, col={col})" };

        var objectId = db.GetObjectId(false, tableHandle, 0);
        var table = (Table)tr.GetObject(objectId, OpenMode.ForWrite);
        var owningBtr = ResolveOwnerBtr(tr, table.OwnerId);

        if (row < table.Rows.Count && col < table.Columns.Count)
        {
            var cell = table.Cells[row, col];
            cell.Value = entity.TranslatedText;
            return new EntityReplaceResult
            {
                Success = true, ModifiedEntity = null, OwningBlock = owningBtr, OriginalHeight = DefaultTextHeight
            };
        }

        return new EntityReplaceResult { Success = false, Error = "Table cell out of range" };
    }

    /// <summary>
    /// Resolves the owning BlockTableRecord from an ObjectId, returning null on failure.
    /// Eliminates 3x duplicated try/catch pattern.
    /// </summary>
    private static BlockTableRecord? ResolveOwnerBtr(Transaction tr, ObjectId ownerId)
    {
        try { return ownerId.IsValid ? tr.GetObject(ownerId, OpenMode.ForRead) as BlockTableRecord : null; }
        catch { return null; }
    }

    /// <summary>
    /// Assigns a mapped, standalone text style to a single entity. The style is
    /// resolved (or created) by <see cref="AcadFontApplier.ResolveStyleId"/> from the
    /// entity's original style name; the shared source style record is never mutated,
    /// so other entities using the same style are unaffected.
    /// </summary>
    private void AssignMappedTextStyle(Entity target, string originalStyleName, Database db, Transaction tr)
    {
        try
        {
            var styleId = AcadFontApplier.ResolveStyleId(db, originalStyleName, _targetIsCjk, tr);
            if (!styleId.HasValue || !styleId.Value.IsValid) return;

            switch (target)
            {
                case DBText dbText: dbText.TextStyleId = styleId.Value; break;
                case MText mText: mText.TextStyleId = styleId.Value; break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for style {Style}", originalStyleName);
        }
    }

    private static string CreateBackup(Database db)
    {
        var originalPath = db.Filename;
        var backupPath = Path.ChangeExtension(originalPath, ".bak");
        try
        {
            // Overwrite in place via a temp copy so a crash mid-copy cannot
            // destroy the previous rollback point.
            if (File.Exists(backupPath))
            {
                var tempPath = backupPath + ".tmp";
                File.Copy(originalPath, tempPath, overwrite: true);
                File.Copy(tempPath, backupPath, overwrite: true);
                File.Delete(tempPath);
            }
            else
            {
                File.Copy(originalPath, backupPath);
            }
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
            var restorePath = Path.ChangeExtension(db.Filename, ".restored.dwg");
            File.Copy(backupPath, restorePath, overwrite: true);
            Log.Warning("Backup restored to {Path} (original file is locked by AutoCAD)", restorePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore backup");
        }
    }
}
