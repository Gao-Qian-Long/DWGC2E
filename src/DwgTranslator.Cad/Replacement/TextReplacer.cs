using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using DwgTranslator.Core.Models;
using Serilog;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// Replaces text in DWG entities with translated text.
/// Handles font mapping and auto-scaling for text overflow.
/// </summary>
public class TextReplacer
{
    private readonly double _autoScaleThreshold;
    private readonly double _autoScaleFactor;

    // Font mapping rules
    private static readonly Dictionary<string, string> CnToEnFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "SimHei", "Arial" },
        { "SimSun", "Arial" },
        { "宋体", "Arial" },
        { "黑体", "Arial" },
        { "gbcbig.shx", "simplex.shx" }
    };

    private static readonly Dictionary<string, string> EnToCnFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Arial", "SimHei" },
        { "Helvetica", "SimHei" },
        { "simplex.shx", "gbcbig.shx" }
    };

    public TextReplacer(double autoScaleThreshold = 1.5, double autoScaleFactor = 0.95)
    {
        _autoScaleThreshold = autoScaleThreshold;
        _autoScaleFactor = autoScaleFactor;
    }

    /// <summary>
    /// Replace text in a database with translated text.
    /// Creates a backup before modification.
    /// </summary>
    public WritebackResult ReplaceAll(Database db, List<TextEntity> entities)
    {
        var result = new WritebackResult();

        // Create backup
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
                    Log.Warning("Failed to replace entity {Handle}: {Error}",
                        entity.Handle, replaceResult.Error);
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
            // Parse handle - special format for attributes
            if (entity.Handle.Contains('/'))
                return ReplaceAttribute(tr, db, entity);

            if (entity.Handle.Contains(':'))
                return ReplaceTableCell(tr, db, entity);

            var handle = new Handle(Convert.ToInt64(entity.Handle, 16));
            var objectId = db.GetObjectId(false, handle, 0);
            var dbObject = tr.GetObject(objectId, OpenMode.ForWrite);

            switch (dbObject)
            {
                case DBText dbText:
                    return ReplaceDBText(dbText, entity);

                case MText mText:
                    return ReplaceMText(mText, entity, db);

                case Dimension dim:
                    return ReplaceDimension(dim, entity);

                case MLeader mLeader:
                    return ReplaceMLeader(mLeader, entity, db);

                default:
                    return new EntityReplaceResult { Success = false, Error = "Unsupported entity type" };
            }
        }
        catch (Exception ex)
        {
            return new EntityReplaceResult { Success = false, Error = ex.Message };
        }
    }

    private EntityReplaceResult ReplaceDBText(DBText dbText, TextEntity entity)
    {
        dbText.TextString = entity.TranslatedText;

        // Auto-scale if needed
        var newWidth = EstimateTextWidth(entity.TranslatedText, dbText.Height);
        if (entity.OriginalWidth > 0 && newWidth > entity.OriginalWidth * _autoScaleThreshold)
        {
            dbText.Height *= entity.OriginalWidth / newWidth * _autoScaleFactor;
        }

        return new EntityReplaceResult { Success = true };
    }

    private EntityReplaceResult ReplaceMText(MText mText, TextEntity entity, Database db)
    {
        mText.Contents = entity.TranslatedText;

        // Map font if needed
        MapTextStyle(mText.TextStyleId, db);

        // Auto-scale
        if (entity.OriginalWidth > 0)
        {
            var newWidth = mText.ActualWidth;
            if (newWidth > entity.OriginalWidth * _autoScaleThreshold)
            {
                mText.TextHeight *= entity.OriginalWidth / newWidth * _autoScaleFactor;
            }
        }

        return new EntityReplaceResult { Success = true };
    }

    private EntityReplaceResult ReplaceDimension(Dimension dim, TextEntity entity)
    {
        dim.DimensionText = entity.TranslatedText;
        return new EntityReplaceResult { Success = true };
    }

    private EntityReplaceResult ReplaceMLeader(MLeader mLeader, TextEntity entity, Database db)
    {
        if (mLeader.MText != null)
        {
            mLeader.MText.Contents = entity.TranslatedText;
            MapTextStyle(mLeader.MText.TextStyleId, db);
        }
        return new EntityReplaceResult { Success = true };
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

        foreach (ObjectId attId in blockRef.AttributeCollection)
        {
            var attRef = (AttributeReference)tr.GetObject(attId, OpenMode.ForWrite);
            if (attRef.Tag == attTag)
            {
                attRef.TextString = entity.TranslatedText;

                // Auto-scale
                var newWidth = EstimateTextWidth(entity.TranslatedText, attRef.Height);
                if (entity.OriginalWidth > 0 && newWidth > entity.OriginalWidth * _autoScaleThreshold)
                {
                    attRef.Height *= entity.OriginalWidth / newWidth * _autoScaleFactor;
                }

                // Sync attributes
                blockRef.RecordGraphicsModified(true);
                return new EntityReplaceResult { Success = true };
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

        if (row < table.Rows.Count && col < table.Columns.Count)
        {
            var cell = table.Cells[row, col];
            // In AutoCAD 2021, set text via Value property
            cell.Value = entity.TranslatedText;
            return new EntityReplaceResult { Success = true };
        }

        return new EntityReplaceResult { Success = false, Error = "Table cell out of range" };
    }

    private void MapTextStyle(ObjectId styleId, Database db)
    {
        if (!styleId.IsValid) return;

        try
        {
            var style = (TextStyleTableRecord)styleId.GetObject(OpenMode.ForWrite);

            // Check if this style name has a CJK-to-English mapping
            if (!CnToEnFonts.TryGetValue(style.Name, out var mappedFontName))
                return;

            // Determine if the mapped font is a SHX or TrueType font
            bool isShx = mappedFontName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase);

            if (isShx)
            {
                // SHX font: set the FileName property
                style.FileName = mappedFontName;
                style.BigFontFileName = string.Empty;
                Log.Information("Font mapped (SHX): {Style} -> {Font}", style.Name, mappedFontName);
            }
            else
            {
                // TrueType font: create a new FontDescriptor
                var currentFont = style.Font;
                var newFont = new FontDescriptor(mappedFontName, currentFont.Bold, currentFont.Italic, 0, 0);
                style.Font = newFont;
                Log.Information("Font mapped (TrueType): {Style} -> {Font}", style.Name, mappedFontName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Font mapping failed for style {Id}", styleId.Handle);
        }
    }

    private string CreateBackup(Database db)
    {
        var originalPath = db.Filename;
        var backupPath = Path.ChangeExtension(originalPath, ".bak");

        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
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

    private void RestoreBackup(Database db, string backupPath)
    {
        if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath))
            return;

        try
        {
            var originalPath = db.Filename;
            // CloseForRestore not available in AutoCAD 2021, just overwrite the file
            File.Copy(backupPath, originalPath, overwrite: true);
            Log.Information("Backup restored from {Path}", backupPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore backup");
        }
    }

    private static double EstimateTextWidth(string text, double height)
    {
        return (text?.Length ?? 0) * height * 0.6;
    }
}

/// <summary>
/// Result of a writeback operation.
/// </summary>
public class WritebackResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public int SkippedCount { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Result of a single entity replacement.
/// </summary>
internal class EntityReplaceResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
}
