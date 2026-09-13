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
    public CadWriteResult WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool targetIsCjk = true, CancellationToken cancellationToken = default, WritebackOptions? options = null)
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
        options ??= new WritebackOptions();
        if (string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(outputFilePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must not overwrite the source drawing.");
        if (options.BackupSource) CreateBackup(sourceFilePath);

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

        Log.Information("Writing translations to {Format}: {Source} -> {Output} ({Count} entities, targetIsCjk={Dir})",
            isDxfOutput ? "DXF" : "DWG", sourceFilePath, outputFilePath, entities.Count, targetIsCjk);

        cancellationToken.ThrowIfCancellationRequested();

        string? tempOutputPath = null;
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
            DwgFontManager.EnsureFontStyles(doc, targetIsCjk);

            // 4. Build lookup: handle → translated text
            var translationMap = BuildTranslationMap(entities);
            Log.Information("Translation map: {Count} entities to replace", translationMap.Count);

            // 5. Process all entity collections: ModelSpace, layouts, block definitions
            var modelFrames = DwgFrameDetector.DetectFrames(doc.ModelSpace.Entities, doc);
            int modelSpaceCount = DwgTextReplacer.ProcessEntityCollection(
                doc.ModelSpace.Entities, translationMap, result, targetIsCjk, doc, modelFrames);
            Log.Information("ModelSpace: {Count} replacements", modelSpaceCount);

            foreach (var layout in doc.Layouts)
            {
                if (layout.Name == "Model" || layout.AssociatedBlock == null) continue;
                var layoutFrames = DwgFrameDetector.DetectFrames(layout.AssociatedBlock.Entities, doc);
                int layoutCount = DwgTextReplacer.ProcessEntityCollection(
                    layout.AssociatedBlock.Entities, translationMap, result, targetIsCjk, doc, layoutFrames);
                if (layoutCount > 0)
                    Log.Debug("Layout '{Name}': {Count} replacements", layout.Name, layoutCount);
            }

            foreach (var blockRecord in doc.BlockRecords)
            {
                if (blockRecord == null) continue;
                if (blockRecord.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockRecord.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                var blockFrames = DwgFrameDetector.DetectFrames(blockRecord.Entities, doc);
                int blockCount = DwgTextReplacer.ProcessEntityCollection(
                    blockRecord.Entities, translationMap, result, targetIsCjk, doc, blockFrames);
                if (blockCount > 0)
                    Log.Debug("Block '{Name}': {Count} replacements", blockRecord.Name, blockCount);
            }

            // 7. Log any unmatched entities
            LogUnmatchedHandles(translationMap);
            result.FailedHandles.AddRange(translationMap.Keys);
            result.FailCount += translationMap.Count;
            if (result.SuccessCount == 0) throw new InvalidOperationException("No translation could be written.");

            // 8. Save the modified document
            EnsureOutputDirectory(outputFilePath);
            cancellationToken.ThrowIfCancellationRequested();

            if (!isDxfOutput && doc.Header.Version == ACadVersion.AC1021)
            {
                Log.Information("Converting unsupported DWG version AC1021 to AC1024 for output");
                doc.Header.Version = ACadVersion.AC1024;
            }

            Log.Information("Writing {Format} output file...", isDxfOutput ? "DXF" : "DWG");
            tempOutputPath = outputFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteDocument(doc, tempOutputPath, isDxfOutput);
            if (!File.Exists(tempOutputPath) || new FileInfo(tempOutputPath).Length == 0)
                throw new IOException("CAD writer produced an empty output file");

            cancellationToken.ThrowIfCancellationRequested();
            SafeFileCommit.Commit(tempOutputPath, outputFilePath, options.OverwriteExisting);
            tempOutputPath = null;

            Log.Information("{Format} writeback complete: {Success} replaced, {Failed} failed",
                isDxfOutput ? "DXF" : "DWG", result.SuccessCount, result.FailCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Format} writeback failed", isDxfOutput ? "DXF" : "DWG");
            result.SuccessCount = 0;
            result.FailCount = entities.Count;
            result.Errors.Add($"Writeback error: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempOutputPath))
            {
                try { if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath); }
                catch (Exception cleanupEx) { Log.Debug(cleanupEx, "Failed to remove temporary output {Path}", tempOutputPath); }
            }
        }

        return result;
    }

    /// <inheritdoc/>
    CadWriteResult IDxfWriterService.WriteTranslations(string sourceFilePath, string outputFilePath, List<CoreTextEntity> entities, bool targetIsCjk, CancellationToken cancellationToken, WritebackOptions? options)
    {
        return WriteTranslations(sourceFilePath, outputFilePath, entities, targetIsCjk, cancellationToken, options);
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
            throw new IOException("Required source backup could not be created.", ex);
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
            if (entity.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
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
