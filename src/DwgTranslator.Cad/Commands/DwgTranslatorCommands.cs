using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Cad.Extraction;
using DwgTranslator.Cad.Replacement;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Cad;
using DwgTranslator.Core.Translation;
using System.Text.Json;
using Exception = System.Exception;

[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.DwgTranslatorCommands))]

namespace DwgTranslator.Cad.Commands;

/// <summary>
/// AutoCAD command methods for DWG Translator.
/// </summary>
public class DwgTranslatorCommands
{
    private static AppConfig? _config;
    private static readonly Dictionary<string, List<TextEntity>> _extractedEntitiesByDoc = new();

    /// <summary>
    /// TESTCMD - Verify CAD API connectivity.
    /// </summary>
    [CommandMethod("TESTCMD")]
    public void TestCommand()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        doc.Editor.WriteMessage("\n[DwgTranslator] CAD API connection verified successfully!\n");
        Log.Information("TESTCMD executed - CAD API connectivity confirmed");
    }

    /// <summary>
    /// DWGTXTEXTRACT - Extract all text entities from the current drawing.
    /// </summary>
    [CommandMethod("DWGTXTEXTRACT")]
    public void ExtractText()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;
        try
        {
            LoadConfig();

            editor.WriteMessage("\n[DwgTranslator] Starting text extraction...\n");

            var extractor = new TextExtractor();
            var extractedEntities = extractor.ExtractAll(doc.Database);
            var docKey = doc.Database.Filename ?? doc.Name ?? "default";
            _extractedEntitiesByDoc[docKey] = extractedEntities;

            // Export to Excel
            var config = GetConfig();
            var exportPath = Path.Combine(
                config.ExportDirectory,
                $"translations_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            var excelService = new ExcelService();
            excelService.ExportToExcelAsync(extractedEntities, exportPath).GetAwaiter().GetResult();

            editor.WriteMessage($"\n[DwgTranslator] Extracted {extractedEntities.Count} text entities");
            editor.WriteMessage($"\n[DwgTranslator] Exported to: {exportPath}");
            editor.WriteMessage("\n[DwgTranslator] Please review translations in Excel, then run DWGTRANSLATEBACK\n");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            Log.Error(ex, "Text extraction failed");
            editor.WriteMessage($"\n[DwgTranslator] Error: {ex.Message}\n");
        }
    }

    /// <summary>
    /// DWGTRANSLATE - Translate extracted text using DeepSeek API.
    /// </summary>
    [CommandMethod("DWGTRANSLATE")]
    public void TranslateText()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;
        var docKey = doc.Database.Filename ?? doc.Name ?? "default";

        if (!_extractedEntitiesByDoc.TryGetValue(docKey, out var extractedEntities) || extractedEntities.Count == 0)
        {
            editor.WriteMessage("\n[DwgTranslator] No entities extracted. Run DWGTXTEXTRACT first.\n");
            return;
        }

        try
        {
            var config = GetConfig();
            editor.WriteMessage("\n[DwgTranslator] Starting translation...\n");

            // Initialize services
            var glossaryService = new GlossaryService();
            glossaryService.LoadGlossaryAsync(config.GlossaryPath).GetAwaiter().GetResult();

            var formatCodeParser = new FormatCodeParser();
            var systemPrompt = LoadSystemPrompt();
            using var httpClient = new HttpClient { BaseAddress = new Uri(config.DeepSeekBaseUrl) };
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.DeepSeekApiKey}");
            var deepSeekClient = new DeepSeekClient(httpClient, config.DeepSeekModel);

            var translationService = new TranslationService(
                glossaryService, formatCodeParser, deepSeekClient, systemPrompt,
                config.BatchSize, config.MaxRetryCount);

            // Filter out XREF entities
            var entitiesToTranslate = extractedEntities
                .Where(e => !e.IsXref)
                .ToList();

            var results = translationService.TranslateBatchAsync(
                entitiesToTranslate,
                config.SourceLanguage,
                config.TargetLanguage).GetAwaiter().GetResult();

            // Update entities with translations
            foreach (var result in results)
            {
                var entity = extractedEntities.FirstOrDefault(e => e.Handle == result.Handle);
                if (entity != null)
                {
                    entity.TranslatedText = result.TranslatedText;
                    entity.GlossaryHit = result.GlossaryHit;
                    entity.Status = result.Status;
                }
            }

            // Re-export to Excel with translations
            var exportPath = Path.Combine(
                config.ExportDirectory,
                $"translations_{DateTime.Now:yyyyMMdd_HHmmss}_translated.xlsx");

            var excelService = new ExcelService();
            excelService.ExportToExcelAsync(extractedEntities, exportPath).GetAwaiter().GetResult();

            var successCount = results.Count(r => r.Status == TranslationStatus.Translated);
            editor.WriteMessage($"\n[DwgTranslator] Translated {successCount}/{results.Count} entities");
            editor.WriteMessage($"\n[DwgTranslator] Results exported to: {exportPath}");
            editor.WriteMessage("\n[DwgTranslator] Review translations, then run DWGTRANSLATEBACK\n");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Translation failed");
            editor.WriteMessage($"\n[DwgTranslator] Error: {ex.Message}\n");
        }
    }

    /// <summary>
    /// DWGTRANSLATEBACK - Write translated text back into the drawing.
    /// </summary>
    [CommandMethod("DWGTRANSLATEBACK")]
    public void WritebackText()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;

        try
        {
            LoadConfig();
            var config = GetConfig();

            // Ask for Excel file path
            var excelPath = editor.GetString("\n[DwgTranslator] Enter Excel file path with reviewed translations:");
            if (excelPath.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                return;

            var filePath = excelPath.StringResult;
            if (!File.Exists(filePath))
            {
                editor.WriteMessage($"\n[DwgTranslator] File not found: {filePath}\n");
                return;
            }

            // Import reviewed translations
            var excelService = new ExcelService();
            var entities = excelService.ImportFromExcelAsync(filePath).GetAwaiter().GetResult();

            // Write back
            var replacer = new TextReplacer(cnToEn: true);
            var result = replacer.ReplaceAll(doc.Database, entities);

            editor.WriteMessage($"\n[DwgTranslator] Writeback complete:");
            editor.WriteMessage($"\n  Success: {result.SuccessCount}");
            editor.WriteMessage($"\n  Failed:  {result.FailCount}");
            editor.WriteMessage($"\n  Skipped: {result.SkippedCount}");

            if (!string.IsNullOrEmpty(result.BackupPath))
                editor.WriteMessage($"\n  Backup:  {result.BackupPath}");

            if (result.Errors.Count > 0)
            {
                editor.WriteMessage("\n\n[DwgTranslator] Errors:");
                foreach (var error in result.Errors)
                    editor.WriteMessage($"\n  - {error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Writeback failed");
            editor.WriteMessage($"\n[DwgTranslator] Error: {ex.Message}\n");
        }
    }

    private static void LoadConfig()
    {
        if (_config != null) return;

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        else
        {
            _config = new AppConfig();
        }

        // Decrypt API key if stored with DPAPI protection
        _config.DeepSeekApiKey = AppConfig.DecryptApiKey(_config.DeepSeekApiKey);
    }

    private static string LoadSystemPrompt()
    {
        var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "deepl_context.txt");
        if (File.Exists(promptPath))
            return File.ReadAllText(promptPath);
        return "You are a professional mechanical engineering translator. Translate the text accurately, preserving any placeholders and formatting codes.";
    }

    private static AppConfig GetConfig()
    {
        LoadConfig();
        return _config!;
    }
}
