using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Serilog;

namespace DwgTranslator.Cad.Replacement;

/// <summary>
/// High-precision DWG writeback engine using AutoCAD .NET API.
/// Uses exact GeometricExtents for collision detection and adaptive layout optimization.
/// </summary>
public class AcadWriterEngine
{
    /// <summary>
    /// Writes translated text back into a DWG file using AutoCAD's native Database API.
    /// Automatically detects drawing frames and optimizes text layout to prevent overflow.
    /// </summary>
    public DwgWriteResult WriteTranslations(
        string sourceFilePath,
        string outputFilePath,
        List<TextEntity> entities,
        bool cnToEn = true)
    {
        var result = new DwgWriteResult();

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

                // Detect frame boundaries before modifications
                var frames = FrameDetector.DetectFrames(db);
                Log.Information("AcadWriter: detected {Count} frame(s) in {File}", frames.Count, sourceFilePath);

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
                    successCount += ProcessBlockTableRecord(modelSpace, tr, entityMap, unprocessed, frames, cnToEn);

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
                            // Process both paper space layouts and user-defined blocks
                            if (isPaperSpace || (!btr.IsAnonymous && !name.StartsWith("*")))
                            {
                                successCount += ProcessBlockTableRecord(btr, tr, entityMap, unprocessed, frames, cnToEn);
                            }
                        }
                    }

                    tr.Commit();

                    result.SuccessCount = successCount;
                    result.FailCount = unprocessed.Count;

                    if (unprocessed.Count > 0)
                    {
                        Log.Warning("AcadWriter: skipped {Count} unmatched entities", unprocessed.Count);
                    }
                }

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

    private int ProcessBlockTableRecord(
        BlockTableRecord btr,
        Transaction tr,
        Dictionary<string, TextEntity> entityMap,
        HashSet<string> unprocessed,
        List<Extents3d> frames,
        bool cnToEn)
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
            catch
            {
                continue;
            }

            if (entity == null) continue;

            var handleStr = entity.Handle.ToString();
            if (entityMap.TryGetValue(handleStr, out var textEntity))
            {
                if (ReplaceEntity(entity, textEntity, frames, cnToEn, tr))
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

    private bool ReplaceEntity(Entity entity, TextEntity ourEntity, List<Extents3d> frames, bool cnToEn, Transaction tr)
    {
        try
        {
            var translatedText = ourEntity.TranslatedText;
            if (string.IsNullOrEmpty(translatedText)) return false;

            switch (entity)
            {
                case DBText dbText:
                    dbText.TextString = translatedText;
                    MapFont(dbText, ourEntity.TextStyleName, cnToEn, tr);
                    LayoutOptimizer.OptimizeDBText(dbText, translatedText, ourEntity);
                    return true;

                case MText mtext:
                    // Preserve original line spacing from source drawing
                    if (ourEntity.MTextLineSpacing > 0)
                        mtext.LineSpacingFactor = ourEntity.MTextLineSpacing;
                    if (ourEntity.MTextLineSpacingStyle > 0)
                        mtext.LineSpacingStyle = (LineSpacingStyle)ourEntity.MTextLineSpacingStyle;

                    // Rebuild multi-line structure if original had hard \P breaks
                    string contents = ourEntity.MTextHasHardBreaks
                        ? RebuildMTextWithLineBreaks(translatedText, ourEntity.MTextLineCount)
                        : translatedText;

                    mtext.Contents = contents
                        .Replace("\r\n", "\\P")
                        .Replace("\n", "\\P")
                        .Replace("\r", "\\P");

                    MapFont(mtext, ourEntity.TextStyleName, cnToEn, tr);

                    var closestFrame = CollisionDetector.FindClosestFrame(mtext.Location, frames);
                    LayoutOptimizer.OptimizeMText(mtext, translatedText, ourEntity, closestFrame, tr);
                    return true;

                case Dimension dim:
                    dim.DimensionText = translatedText;
                    return true;

                // MultiLeader handling omitted due to API version compatibility;
                // AutoCAD 2021 uses MLeader which requires additional reference checks.

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

    private static void MapFont(DBText text, string originalStyleName, bool cnToEn, Transaction tr)
    {
        try
        {
            string? targetFont = MapFontName(originalStyleName, cnToEn);
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
                // Create new style
                textStyleTable.UpgradeOpen();
                var newStyle = new TextStyleTableRecord { Name = targetFont };
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
            string? targetFont = MapFontName(originalStyleName, cnToEn);
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

    private static string? MapFontName(string currentStyleName, bool cnToEn)
    {
        if (cnToEn)
        {
            if (currentStyleName.Contains("SimHei", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("SimSun", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("宋体", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("黑体", StringComparison.OrdinalIgnoreCase))
                return "Arial";
        }
        else
        {
            if (currentStyleName.Contains("Arial", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("Helvetica", StringComparison.OrdinalIgnoreCase) ||
                currentStyleName.Contains("Times", StringComparison.OrdinalIgnoreCase))
                return "SimHei";
        }
        return null;
    }

    /// <summary>
    /// Rebuilds MText content with \P line breaks to match the original line count.
    /// Distributes translated text roughly evenly across the original number of lines.
    /// </summary>
    private static string RebuildMTextWithLineBreaks(string translatedText, int targetLineCount)
    {
        if (targetLineCount <= 1 || string.IsNullOrEmpty(translatedText))
            return translatedText;

        // Split into words (handle both English spaces and CJK characters)
        var segments = new List<string>();
        var currentWord = new System.Text.StringBuilder();
        foreach (char c in translatedText)
        {
            if (c == ' ')
            {
                if (currentWord.Length > 0)
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                }
                // Keep spaces as separate segments to preserve spacing
            }
            else if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter &&
                     c >= 0x4E00 && c <= 0x9FFF)
            {
                // CJK characters - each character is a segment
                if (currentWord.Length > 0 &&
                    !(char.GetUnicodeCategory(currentWord[0]) == System.Globalization.UnicodeCategory.OtherLetter && currentWord[0] >= 0x4E00))
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                }
                if (currentWord.Length > 0)
                {
                    segments.Add(currentWord.ToString());
                    currentWord.Clear();
                }
                segments.Add(c.ToString());
            }
            else
            {
                currentWord.Append(c);
            }
        }
        if (currentWord.Length > 0)
            segments.Add(currentWord.ToString());

        if (segments.Count == 0)
            return translatedText;

        // Distribute segments evenly across target lines
        int segmentsPerLine = Math.Max(1, (int)Math.Ceiling((double)segments.Count / targetLineCount));
        var lines = new List<string>();
        var lineBuilder = new System.Text.StringBuilder();
        int segCount = 0;

        foreach (var seg in segments)
        {
            lineBuilder.Append(seg);
            segCount++;
            if (segCount >= segmentsPerLine && lines.Count < targetLineCount - 1)
            {
                lines.Add(lineBuilder.ToString().TrimEnd());
                lineBuilder.Clear();
                segCount = 0;
            }
        }
        if (lineBuilder.Length > 0)
            lines.Add(lineBuilder.ToString().TrimEnd());

        return string.Join("\\P", lines);
    }
}
