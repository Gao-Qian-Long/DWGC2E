#if GSTARCAD
using Gssoft.Gscad.ApplicationServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
#endif
#if GSTARCAD
using Gssoft.Gscad.EditorInput;
#else
using Autodesk.AutoCAD.EditorInput;
#endif
#if GSTARCAD
using Gssoft.Gscad.Runtime;
#else
using Autodesk.AutoCAD.Runtime;
#endif
using DwgTranslator.Cad.Replacement;
using DwgTranslator.Core.Models;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.WritebackCommand))]

namespace DwgTranslator.Cad.Commands;

/// <summary>
/// AutoCAD command entry point for high-precision DWG writeback.
/// The WPF app passes a session-specific config path as the first command argument.
/// A legacy fixed path is retained only for manually invoked older integrations.
/// </summary>
public class WritebackCommand
{
    private class WritebackConfig
    {
        public string SourceDwgPath { get; set; } = "";
        public string OutputDwgPath { get; set; } = "";
        public bool OverwriteExisting { get; set; }
        public List<TextEntity> Entities { get; set; } = new();

        /// <summary>
        /// True when the translation is written into a CJK language, so the font mapping keeps a
        /// CJK-capable text style. Nullable so an older app that only sends the legacy "cnToEn"
        /// field still resolves; both spellings are accepted because JavaScriptSerializer matches
        /// property names case-insensitively.
        /// </summary>
        public bool? TargetIsCjk { get; set; }
        public bool? CnToEn { get; set; }

        /// <summary>Resolved direction flag, defaulting to CJK output for a config with neither field.</summary>
        public bool WritesCjkText => TargetIsCjk ?? (CnToEn.HasValue ? !CnToEn.Value : true);

        public string? SessionId { get; set; }
        public string? DoneSignalPath { get; set; }
    }

    private static readonly string LegacyConfigDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
    private static readonly string LegacyConfigPath = Path.Combine(LegacyConfigDir, "writeback_config.json");
    private static readonly string LegacyDonePath = Path.Combine(LegacyConfigDir, "writeback_done.txt");

    [CommandMethod("DwgTranslateWrite")]
    public void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        string configPath = LegacyConfigPath;
        string donePath = LegacyDonePath;
        string? sessionId = null;

        try
        {
            var prompt = new PromptStringOptions("\nWriteback config path <legacy>: ")
            {
                AllowSpaces = true,
                UseDefaultValue = true,
                DefaultValue = LegacyConfigPath
            };
            var promptResult = ed.GetString(prompt);
            if (promptResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(promptResult.StringResult))
                configPath = promptResult.StringResult.Trim().Trim('"');

            if (!File.Exists(configPath))
            {
                ed.WriteMessage($"\nNo writeback config file found: {configPath}");
                SignalDone(donePath, "no_config", sessionId);
                return;
            }

            var json = File.ReadAllText(configPath);
#if NETFRAMEWORK
            var config = new JavaScriptSerializer().Deserialize<WritebackConfig>(json);
#else
            var config = JsonSerializer.Deserialize<WritebackConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
#endif

            sessionId = config?.SessionId;
            var configuredDonePath = config?.DoneSignalPath;
            donePath = !string.IsNullOrWhiteSpace(configuredDonePath)
                ? configuredDonePath!
                : Path.Combine(Path.GetDirectoryName(configPath) ?? LegacyConfigDir, "writeback_done.txt");

            if (config == null || config.Entities == null || config.Entities.Count == 0)
            {
                ed.WriteMessage("\nConfig file invalid or has no entities to write back.");
                SignalDone(donePath, "invalid_config", sessionId);
                return;
            }

            if (string.IsNullOrEmpty(config.SourceDwgPath) || !File.Exists(config.SourceDwgPath))
            {
                ed.WriteMessage($"\nSource DWG not found: {config.SourceDwgPath}");
                SignalDone(donePath, "source_missing", sessionId);
                return;
            }

            if (string.IsNullOrEmpty(config.OutputDwgPath))
            {
                ed.WriteMessage("\nOutput path is empty.");
                SignalDone(donePath, "no_output", sessionId);
                return;
            }

            ed.WriteMessage($"\nStarting writeback: {config.Entities.Count} entities...");

            // AcadWriterEngine opens the source read-only and saves atomically through
            // a separate output file. Passing the source directly avoids GstarCAD
            // retaining a lock on a session-local copy until the command fully exits.
            var engine = new AcadWriterEngine();
            var result = engine.WriteTranslations(
                config.SourceDwgPath, config.OutputDwgPath,
                config.Entities, config.WritesCjkText, new WritebackOptions { OverwriteExisting = config.OverwriteExisting });

            if (result.SuccessCount > 0)
            {
                ed.WriteMessage($"\nWriteback complete: {result.SuccessCount} success, {result.FailCount} failed.");
                try { File.Delete(configPath); } catch { }
            }
            else
            {
                var errors = result.Errors.Count > 0
                    ? string.Join(", ", result.Errors.Take(3))
                    : "(no detailed error info)";
                ed.WriteMessage($"\nWriteback failed: {errors}");
            }

            SignalDone(
                donePath,
                result.SuccessCount > 0 && result.FailCount > 0 ? "partial" :
                    result.SuccessCount > 0 ? "success" : "failed",
                sessionId,
                result.SuccessCount,
                result.FailCount,
                string.Join("; ", result.Errors.Take(5)));
        }
        catch (System.Exception ex)
        {
            // Log the failure as well as reporting it on the command line: a batch writeback runs
            // with its console hidden, so without this the only trace of a broken config or a
            // missing dependency is the terse done-signal status.
            Log.Error(ex, "DwgTranslateWrite failed for config {Config}: {Message}", configPath, ex.Message);
            ed.WriteMessage($"\nCommand error: {ex.Message}");
            SignalDone(donePath, "error", sessionId, 0, 0, ex.Message);
        }
    }

    private static void SignalDone(
        string donePath,
        string status,
        string? sessionId = null,
        int successCount = 0,
        int failCount = 0,
        string detail = "")
    {
        try
        {
            var directory = Path.GetDirectoryName(donePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var sid = sessionId ?? "none";
            var content = $"{status}|{sid}|{successCount}|{failCount}|{DateTime.UtcNow:O}|{detail.Replace('|', ';').Replace('\r', ' ').Replace('\n', ' ')}";
            var temporary = donePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, content);
            DwgTranslator.Core.Services.SafeFileCommit.Commit(temporary, donePath, true);
        }
        catch { }
    }
}
