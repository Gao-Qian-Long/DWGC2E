#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.DatabaseServices;
#else
using Autodesk.AutoCAD.DatabaseServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif
using DwgTranslator.Cad.Extraction;
using DwgTranslator.Cad.Replacement;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Cad;
using DwgTranslator.Core.Translation;
using System.Text.Json;
using Exception = System.Exception;
#if GSTARCAD
using Gssoft.Gscad.EditorInput;
using CadRuntimeException = Gssoft.Gscad.Runtime.Exception;
#else
using Autodesk.AutoCAD.EditorInput;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
#endif

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
        catch (CadRuntimeException ex)
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
            var expectedGlossary = TranslationLanguages.GlossaryFileName(
                config.SourceLanguage, config.TargetLanguage);
            var glossaryPath = string.Equals(Path.GetFileName(config.GlossaryPath), expectedGlossary,
                StringComparison.OrdinalIgnoreCase) ? config.GlossaryPath : string.Empty;
            glossaryService.LoadGlossaryAsync(glossaryPath).GetAwaiter().GetResult();

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
            if (excelPath.Status != PromptStatus.OK)
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
            var replacer = new TextReplacer(targetIsCjk: TranslationLanguages.IsCjk(config.TargetLanguage));
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

        // Prefer AppData settings (same as WPF app) so API key/glossary match UI config.
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DwgTranslator", "settings.json");
        var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        var configPath = File.Exists(appDataPath) ? appDataPath : basePath;

        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            _config = JsonSerializer.Deserialize<AppConfig>(json, DwgTranslator.Core.Models.AppConfigJson.ReadOptions) ?? new AppConfig();
            Log.Information("CAD plugin loaded settings from {Path}", configPath);
        }
        else
        {
            _config = new AppConfig();
            Log.Warning("CAD plugin settings.json not found; using defaults");
        }

        // Decrypt API key if stored with DPAPI protection
        _config.DeepSeekApiKey = AppConfig.DecryptApiKey(_config.DeepSeekApiKey);

        // Resolve relative glossary/export paths against AppData when needed
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DwgTranslator");
        if (!string.IsNullOrEmpty(_config.GlossaryPath) && !Path.IsPathRooted(_config.GlossaryPath))
        {
            var appDataGlossary = Path.Combine(appDataDir, _config.GlossaryPath);
            if (File.Exists(appDataGlossary))
                _config.GlossaryPath = appDataGlossary;
        }
        if (!string.IsNullOrEmpty(_config.ExportDirectory) && !Path.IsPathRooted(_config.ExportDirectory))
            _config.ExportDirectory = Path.Combine(appDataDir, _config.ExportDirectory);
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
