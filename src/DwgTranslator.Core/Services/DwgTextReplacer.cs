using ACadSharp;
using ACadSharp.Entities;
using DwgTranslator.Core.Models;
using Serilog;

using CadEntity = ACadSharp.Entities.Entity;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttribute = ACadSharp.Entities.AttributeEntity;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadTable = ACadSharp.Entities.TableEntity;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Handles text replacement in CAD entities: iterates entity collections,
/// matches entities to translated text by handle (O(1) dictionary lookup),
/// dispatches replacement to the correct entity type, and handles nested
/// insert attributes.
/// </summary>
internal static class DwgTextReplacer
{
    /// <summary>
    /// Process a collection of entities, replacing text for matching handles.
    /// Uses pre-built translationMap (Handle→Entity) for O(1) lookup per entity.
    /// Returns count of successful replacements.
    /// </summary>
    public static int ProcessEntityCollection(
        IEnumerable<CadEntity> entities,
        Dictionary<string, CoreTextEntity> translationMap,
        CadWriteResult result,
        bool targetIsCjk,
        CadDocument doc,
        List<(double minX, double minY, double maxX, double maxY)> frames)
    {
        if (translationMap.Count == 0) return 0;
        if (entities == null) return 0;

        var entityList = entities as List<CadEntity> ?? new List<CadEntity>(entities);

        int replacedCount = 0;

        foreach (var cadEntity in entityList)
        {
            if (cadEntity == null) continue;

            var handleStr = FormatHandle(cadEntity.Handle);

            if (translationMap.TryGetValue(handleStr, out var translatedEntity))
            {
                var success = ReplaceEntityText(cadEntity, translatedEntity, targetIsCjk, doc, frames, entityList);
                if (success)
                {
                    result.SuccessCount++;
                    translationMap.Remove(handleStr);
                    replacedCount++;
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

            // Handle table cells via compound handles "HANDLE:row:col"
            if (cadEntity is CadTable table)
            {
                ProcessTableCells(table, translationMap, result, ref replacedCount);
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
                if (string.IsNullOrEmpty(translatedEntity.TranslatedText)) continue;
                // Note: Attributes skip font mapping and scaling intentionally —
                // they inherit their parent block insert's style/scale transform.
                if (!AttributeTranslationPolicy.IsMetadataTag(attEntity.Tag))
                    attEntity.Value = translatedEntity.TranslatedText;
                result.SuccessCount++;
                translationMap.Remove(compoundHandle);
                replacedCount++;
            }
        }
    }


    /// <summary>
    /// Replace text for cells inside a TABLE entity using compound handles "HANDLE:row:col".
    /// </summary>
    private static void ProcessTableCells(
        CadTable table,
        Dictionary<string, CoreTextEntity> translationMap,
        CadWriteResult result,
        ref int replacedCount)
    {
        var tableHandle = FormatHandle(table.Handle);
        // Snapshot keys that belong to this table to avoid modifying while iterating.
        var keys = translationMap.Keys
            .Where(k => k.StartsWith(tableHandle + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            if (!translationMap.TryGetValue(key, out var translatedEntity)) continue;
            if (string.IsNullOrEmpty(translatedEntity.TranslatedText)) continue;

            var parts = key.Split(':');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[1], out var row) || !int.TryParse(parts[2], out var col)) continue;
            if (row < 0 || row >= table.Rows.Count) continue;

            try
            {
                var rowObj = table.Rows[row];
                if (rowObj?.Cells == null || col < 0 || col >= rowObj.Cells.Count) continue;
                var cell = rowObj.Cells[col];
                if (!TrySetTableCellText(cell, translatedEntity.TranslatedText))
                {
                    result.FailCount++;
                    result.Errors.Add($"Failed to replace table cell {key}");
                    continue;
                }

                result.SuccessCount++;
                translationMap.Remove(key);
                replacedCount++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to replace table cell {Key}", key);
                result.FailCount++;
                result.Errors.Add($"Failed to replace table cell {key}: {ex.Message}");
            }
        }
    }

    private static bool TrySetTableCellText(CadTable.Cell cell, string text)
    {
        try
        {
            if (cell.HasMultipleContent && cell.Contents != null && cell.Contents.Count > 0)
            {
                // Never flatten multiple independently formatted/formula contents.
                return false;
            }

            if (cell.Content?.CadValue != null)
            {
                cell.Content.CadValue.SetValue(text);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TrySetTableCellText failed");
        }
        return false;
    }
    /// <summary>
    /// Replace text content of a CAD entity with translated text, applying font mapping and scaling.
    /// </summary>
    public static bool ReplaceEntityText(CadEntity entity, CoreTextEntity ourEntity, bool targetIsCjk, CadDocument doc,
        List<(double minX, double minY, double maxX, double maxY)> frames,
        List<CadEntity> allEntities)
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
                    // Restore original text height before font mapping.
                    if (originalHeight > 0)
                        textEntity.Height = originalHeight;
                    DwgFontManager.ApplyFontMapping(textEntity, ourEntity.TextStyleName, targetIsCjk, doc);
                    if (originalHeight > 0)
                        textEntity.Height = originalHeight;

                    // Pre-layout: keep original height (no aggressive pre-shrink)
                    DwgTextScaler.ApplyScaling(textEntity, translatedText, ourEntity);

                    // Collision resolution: wrap -> nudge -> scale, retest each step
                    if (allEntities.Count > 0 && originalHeight > 0)
                        DwgCollisionDetector.ResolveCollisions(textEntity, originalHeight, allEntities, ourEntity);

                    // Frame-boundary safety last (after collision resolution)
                    if (frames.Count > 0 && originalHeight > 0)
                        DwgFrameDetector.CheckAndScaleToFitFrame(textEntity, frames, originalHeight);
                    return true;

                case CadMText mtext:
                    mtext.Value = FontMapper.MapInlineFonts(translatedText,targetIsCjk).Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
                    if (mtext.HasColumns && mtext.ColumnData != null)
                        mtext.ColumnData.ColumnType = ACadSharp.Entities.ColumnType.NoColumns;
                    if (originalHeight > 0)
                        mtext.Height = originalHeight;
                    DwgFontManager.ApplyFontMapping(mtext, ourEntity.TextStyleName, targetIsCjk, doc);
                    if (originalHeight > 0)
                        mtext.Height = originalHeight;

                    // Pre-layout: keep original height + gentle wrap width
                    DwgTextScaler.ApplyScaling(mtext, translatedText, ourEntity);

                    // Collision resolution: wrap -> nudge -> scale, retest each step
                    if (allEntities.Count > 0 && originalHeight > 0)
                        DwgCollisionDetector.ResolveCollisions(mtext, originalHeight, allEntities, ourEntity);

                    // Frame-boundary safety last
                    if (frames.Count > 0 && originalHeight > 0)
                        DwgFrameDetector.CheckAndScaleToFitFrame(mtext, frames, originalHeight);
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
    /// Formats a numeric (ulong) handle to its canonical string representation (uppercase hexadecimal).
    /// This MUST match how handles are stored in TextEntity.Handle by DwgReaderService,
    /// otherwise lookup will silently fail and no text will be replaced.
    /// </summary>
    public static string FormatHandle(ulong handle) => handle.ToString("X");
}
