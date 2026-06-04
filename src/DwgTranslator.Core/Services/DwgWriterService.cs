using ACadSharp;
using ACadSharp.IO;
using DwgTranslator.Core.Models;
using Serilog;
using CoreTextEntity = DwgTranslator.Core.Models.TextEntity;

namespace DwgTranslator.Core.Services;

/// <summary>
/// Writes translated text back into DWG and DXF files using ACadSharp library (offline, no AutoCAD required).
/// Orchestrates: file I/O → font setup → frame detection → text replacement with scaling/collision → save.
/// Delegates specialized work to focused helper classes: DwgFontManager, DwgFrameDetector,
/// DwgCollisionDetector, DwgTextReplacer, DwgTextScaler, DwgBoundsEstimator.
/// </summary>
public class DwgWriterService : IDwgWriterService, IDxfWriterService
{
    /// <inheritdoc/>
    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool cnToEn = true, CancellationToken cancellationToken = default)
    {
        var result = new CadWriteResult();

        if (!File.Exists(sourceFilePath))
        {
            result.Errors.Add($"Source file not found: {sourceFilePath}");
            Log.Error("Source CAD file not found: {Path}", sourceFilePath);
            return result;
        }

        if (entities.Count == 0)
        {
            result.Errors.Add("No entities to write");
            Log.Warning("No entities provided for writeback");
            return result;
        }

        // 1. Auto-backup original file
        CreateBackup(sourceFilePath);

        // Detect file format
        var sourceExtension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var outputExtension = Path.GetExtension(outputFilePath).ToLowerInvariant();
        var isDxfSource = sourceExtension == ".dxf";
        var isDxfOutput = outputExtension == ".dxf";

        if (isDxfSource)
        {
            bool isBinary = DxfReader.IsBinary(sourceFilePath);
            Log.Information("Source DXF format: {Type}", isBinary ? "Binary" : "ASCII");
        }

        Log.Information("Writing translations to {Format}: {Source} -> {Output} ({Count} entities, CnToEn={Dir})",
            isDxfOutput ? "DXF" : "DWG", sourceFilePath, outputFilePath, entities.Count, cnToEn);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // 2. Read the original file
            var doc = ReadDocument(sourceFilePath, isDxfSource);
            if (doc is null)
            {
                result.Errors.Add($"Failed to read {(isDxfSource ? "DXF" : "DWG")} file");
                return result;
            }

            // 3. Ensure output font styles exist in document
            DwgFontManager.EnsureFontStyles(doc, cnToEn);

            // 4. Build lookup: handle → translated text
            var translationMap = BuildTranslationMap(entities);
            Log.Information("Translation map: {Count} entities to replace", translationMap.Count);

            // 5. Process all entity collections: ModelSpace, layouts, block definitions
            var emptyFrames = new List<(double minX, double minY, double maxX, double maxY)>();
            int modelSpaceCount = DwgTextReplacer.ProcessEntityCollection(
                doc.ModelSpace.Entities, translationMap, result, cnToEn, doc, emptyFrames);
            Log.Information("ModelSpace: {Count} replacements", modelSpaceCount);

            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model" || layout.AssociatedBlock == null) continue;
                int layoutCount = DwgTextReplacer.ProcessEntityCollection(
                    layout.AssociatedBlock.Entities, translationMap, result, cnToEn, doc, emptyFrames);
                if (layoutCount > 0)
                    Log.Debug("Layout '{Name}': {Count} replacements", layout.Name, layoutCount);
            }

            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord == null) continue;
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                int blockCount = DwgTextReplacer.ProcessEntityCollection(
                    blockRecord.Entities, translationMap, result, cnToEn, doc, emptyFrames);
                if (blockCount > 0)
                    Log.Debug("Block '{Name}': {Count} replacements", blockRecord.Name, blockCount);
            }

            // 7. Log any unmatched entities
            LogUnmatchedHandles(translationMap);

            // 8. Save the modified document
            EnsureOutputDirectory(outputFilePath);
            cancellationToken.ThrowIfCancellationRequested();

            Log.Information("Writing {Format} output file...", isDxfOutput ? "DXF" : "DWG");
            WriteDocument(doc, outputFilePath, isDxfOutput);

            Log.Information("{Format} writeback complete: {Success} replaced, {Failed} failed",
                isDxfOutput ? "DXF" : "DWG", result.SuccessCount, result.FailCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Format} writeback failed", isDxfOutput ? "DXF" : "DWG");
            result.Errors.Add($"Writeback error: {ex.Message}");
        }

        return result;
    }

    /// <inheritdoc/>
    CadWriteResult IDxfWriterService.WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool cnToEn, CancellationToken cancellationToken)
    {
        return WriteTranslations(sourceFilePath, outputFilePath, entities, cnToEn, cancellationToken);
    }

    // ───────────────────── Private orchestration helpers ─────────────────────

    private static void CreateBackup(string sourceFilePath)
    {
        try
        {
            var backupPath = sourceFilePath + ".bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(sourceFilePath, backupPath, overwrite: false);
                Log.Information("Backup created: {Backup}", backupPath);
            }
            else
            {
                Log.Debug("Backup already exists, skipping: {Backup}", backupPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create backup for {Path}", sourceFilePath);
        }
    }

    private static CadDocument? ReadDocument(string sourceFilePath, bool isDxfSource)
    {
        if (isDxfSource)
        {
            using var reader = new DxfReader(sourceFilePath, OnCadNotification);
            return reader.Read();
        }
        return DwgReader.Read(sourceFilePath);
    }

    private static Dictionary<string, CoreTextEntity> BuildTranslationMap(List<CoreTextEntity> entities)
    {
        var map = new Dictionary<string, CoreTextEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities)
        {
            if (entity.Status == TranslationStatus.Translated ||
                entity.Status == TranslationStatus.Reviewed)
            {
                var handle = CleanHandle(entity.Handle);
                if (!string.IsNullOrEmpty(handle))
                    map.TryAdd(handle, entity);
            }
        }
        return map;
    }

    private static void LogUnmatchedHandles(Dictionary<string, CoreTextEntity> translationMap)
    {
        int unprocessedCount = translationMap.Count;
        if (unprocessedCount == 0) return;
        Log.Warning("Skipped {Count} entities: handles not found in DWG", unprocessedCount);
        foreach (var kv in translationMap)
            Log.Debug("  Unmatched handle: {Handle}", kv.Key);
    }

    private static void EnsureOutputDirectory(string outputFilePath)
    {
        var outputDir = Path.GetDirectoryName(outputFilePath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
    }

    private static void WriteDocument(CadDocument doc, string outputFilePath, bool isDxfOutput)
    {
        if (isDxfOutput)
        {
            var dxfConfig = new DxfWriterConfiguration();
            DxfWriter.Write(outputFilePath, doc, false, dxfConfig, OnCadNotification);
        }
        else
        {
            using var writer = new DwgWriter(outputFilePath, doc);
            writer.Write();
        }
    }

    /// <summary>
    /// Clean a handle string for comparison.
    /// </summary>
    private static string CleanHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        // Preserve compound handles (attributes: "handle/tag", table cells: "handle:row:col")
        if (handle.Contains('/') || handle.Contains(':')) return handle;
        return handle.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Handles ACadSharp notification events during CAD file operations.
    /// </summary>
    private static void OnCadNotification(object sender, ACadSharp.IO.NotificationEventArgs e)
    {
        if (e.Exception != null)
            Log.Warning(e.Exception, "ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
        else
            Log.Debug("ACadSharp [{Type}]: {Message}", e.NotificationType, e.Message);
    }
}
