using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Header;
using DwgTranslator.Core.Models;
using Serilog;
using System.Text.RegularExpressions;
using CadEntity = ACadSharp.Entities.Entity;
using CadText = ACadSharp.Entities.TextEntity;
using CadMText = ACadSharp.Entities.MText;
using CadDimension = ACadSharp.Entities.Dimension;
using CadMultiLeader = ACadSharp.Entities.MultiLeader;
using CadInsert = ACadSharp.Entities.Insert;
using CadAttribute = ACadSharp.Entities.AttributeEntity;
using OurTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Writes translated text back into DWG files using ACadSharp library (offline, no AutoCAD required).
/// Key fix: skips already-processed ModelSpace/PaperSpace blocks to avoid duplicate entity modification
/// which causes DWG corruption.
/// </summary>
public class DwgWriterService : IDwgWriterService
{
    private static readonly Regex HandleRegex = new(@"^[0-9A-Fa-f]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public DwgWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<OurTextEntity> entities)
    {
        var result = new DwgWriteResult();

        if (!File.Exists(sourceFilePath))
        {
            result.Errors.Add($"Source file not found: {sourceFilePath}");
            Log.Error("Source DWG file not found: {Path}", sourceFilePath);
            return result;
        }

        if (entities.Count == 0)
        {
            result.Errors.Add("No entities to write");
            Log.Warning("No entities provided for writeback");
            return result;
        }

        Log.Information("Writing translations to DWG: {Source} -> {Output} ({Count} entities)",
            sourceFilePath, outputFilePath, entities.Count);

        try
        {
            // Read the original DWG
            var doc = DwgReader.Read(sourceFilePath);
            if (doc == null)
            {
                result.Errors.Add("Failed to read DWG file");
                return result;
            }

            // Build lookup: handle -> translated text
            var translationMap = new Dictionary<string, OurTextEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (entity.Status == TranslationStatus.Translated ||
                    entity.Status == TranslationStatus.Reviewed)
                {
                    var handle = CleanHandle(entity.Handle);
                    if (!string.IsNullOrEmpty(handle) && !translationMap.ContainsKey(handle))
                        translationMap[handle] = entity;
                }
            }

            Log.Information("Translation map: {Count} entities to replace", translationMap.Count);

            // Track which block names we've already processed to avoid duplicates
            var processedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "*Model_Space",
                "*Paper_Space"
            };

            // Process model space (directly - NOT via BlockRecords)
            int modelSpaceCount = ProcessEntityCollection(doc.ModelSpace.Entities, translationMap, result);
            Log.Information("ModelSpace: {Count} replacements", modelSpaceCount);

            // Process all layouts (paper space) directly
            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model") continue;
                if (layout.AssociatedBlock == null) continue;

                int layoutCount = ProcessEntityCollection(
                    layout.AssociatedBlock.Entities, translationMap, result);
                if (layoutCount > 0)
                    Log.Debug("Layout '{Name}': {Count} replacements", layout.Name, layoutCount);
            }

            // Process nested blocks only (skip Model_Space and Paper_Space which are already done)
            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord == null) continue;
                if (processedBlocks.Contains(blockRecord.Name)) continue;

                processedBlocks.Add(blockRecord.Name);

                int blockCount = ProcessEntityCollection(blockRecord.Entities, translationMap, result);
                if (blockCount > 0)
                    Log.Debug("Block '{Name}': {Count} replacements", blockCount, blockRecord.Name);
            }

            int unprocessedCount = translationMap.Count;
            if (unprocessedCount > 0)
            {
                Log.Warning("Skipped {Count} entities: handles not found in DWG", unprocessedCount);
                foreach (var kv in translationMap)
                    Log.Debug("  Unmatched handle: {Handle}", kv.Key);
            }

            // Save the modified document
            var outputDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            Log.Information("Writing DWG output file...");
            using (var writer = new DwgWriter(outputFilePath, doc))
            {
                writer.Write();
            }

            Log.Information("DWG writeback complete: {Success} replaced, {Failed} failed",
                result.SuccessCount, result.FailCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DWG writeback failed");
            result.Errors.Add($"Writeback error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Process a collection of entities, replacing text for matching handles.
    /// Returns count of successful replacements.
    /// </summary>
    private static int ProcessEntityCollection(
        IEnumerable<CadEntity> entities,
        Dictionary<string, OurTextEntity> translationMap,
        DwgWriteResult result)
    {
        int replacedCount = 0;

        foreach (var cadEntity in entities)
        {
            if (cadEntity == null) continue;

            // Use same decimal format as DwgReaderService
            var handleStr = cadEntity.Handle.ToString();

            if (translationMap.TryGetValue(handleStr, out var translatedEntity))
            {
                var success = ReplaceEntityText(cadEntity, translatedEntity.TranslatedText);
                if (success)
                {
                    result.SuccessCount++;
                    translationMap.Remove(handleStr);
                    replacedCount++;
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
            }
        }

        return replacedCount;
    }

    /// <summary>
    /// Replace text for attributes inside a block insert.
    /// </summary>
    private static void ProcessInsertAttributes(
        CadInsert insert,
        Dictionary<string, OurTextEntity> translationMap,
        DwgWriteResult result,
        ref int replacedCount)
    {
        foreach (var att in insert.Attributes)
        {
            if (att is not CadAttribute attEntity) continue;

            // Attribute handle format: "OwnerHandle/Tag" (matches DwgReaderService)
            var compoundHandle = $"{insert.Handle}/{attEntity.Tag}";

            if (translationMap.TryGetValue(compoundHandle, out var translatedEntity))
            {
                attEntity.Value = translatedEntity.TranslatedText;
                result.SuccessCount++;
                translationMap.Remove(compoundHandle);
                replacedCount++;
            }
        }
    }

    /// <summary>
    /// Replace text content of a CAD entity with translated text.
    /// </summary>
    private static bool ReplaceEntityText(CadEntity entity, string translatedText)
    {
        try
        {
            switch (entity)
            {
                case CadText textEntity when entity is not CadMText:
                    textEntity.Value = translatedText;
                    return true;

                case CadMText mtext:
                    mtext.Value = translatedText;
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
    /// Clean a handle string for comparison.
    /// </summary>
    private static string CleanHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        if (handle.Contains('/') || handle.Contains(':')) return handle;
        return handle;
    }
}

/// <summary>
/// Result of a DWG writeback operation.
/// </summary>
public class DwgWriteResult
{
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsSuccess => FailCount == 0 && Errors.Count == 0;
}