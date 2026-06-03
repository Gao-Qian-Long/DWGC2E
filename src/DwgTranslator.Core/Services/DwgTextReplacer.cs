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
        bool cnToEn,
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
                var success = ReplaceEntityText(cadEntity, translatedEntity, cnToEn, doc, frames);
                if (success)
                {
                    result.SuccessCount++;
                    translationMap.Remove(handleStr);
                    replacedCount++;

                    // Entity-level collision detection: after text replacement,
                    // check whether the new text overlaps nearby geometry and scale
                    // height down via binary search if it does.
                    double originalHeight = translatedEntity.OriginalHeight > 0 ? translatedEntity.OriginalHeight : 0;
                    if (originalHeight > 0)
                        DwgCollisionDetector.ScaleDownToAvoidCollisions(cadEntity, originalHeight, entityList);

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
                attEntity.Value = translatedEntity.TranslatedText;
                result.SuccessCount++;
                translationMap.Remove(compoundHandle);
                replacedCount++;
            }
        }
    }

    /// <summary>
    /// Replace text content of a CAD entity with translated text, applying font mapping and scaling.
    /// </summary>
    public static bool ReplaceEntityText(CadEntity entity, CoreTextEntity ourEntity, bool cnToEn, CadDocument doc,
        List<(double minX, double minY, double maxX, double maxY)> frames)
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
                    DwgFontManager.ApplyFontMapping(textEntity, ourEntity.TextStyleName, cnToEn, doc);
                    DwgTextScaler.ApplyScaling(textEntity, translatedText, ourEntity);
                    if (frames.Count > 0 && originalHeight > 0)
                        DwgFrameDetector.CheckAndScaleToFitFrame(textEntity, frames, originalHeight);
                    return true;

                case CadMText mtext:
                    mtext.Value = translatedText.Replace("\r\n", "\\P").Replace("\n", "\\P").Replace("\r", "\\P");
                    DwgFontManager.ApplyFontMapping(mtext, ourEntity.TextStyleName, cnToEn, doc);
                    DwgTextScaler.ApplyScaling(mtext, translatedText, ourEntity);
                    if (frames.Count > 0 && originalHeight > 0)
                        DwgFrameDetector.CheckAndScaleToFitFrame(mtext, frames, originalHeight);
                    return true;

                case CadDimension dim:
                    // TODO: Add ApplyFontMapping overload for CadDimension.
                    // DwgFontManager only has overloads for CadText and CadMText;
                    // Dimension.Style is DimensionStyle (not TextStyle), so a
                    // dedicated overload or different approach is needed.
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
