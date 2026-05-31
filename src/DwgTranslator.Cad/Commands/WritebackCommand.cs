using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Cad.Replacement;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;

[assembly: CommandClass(typeof(DwgTranslator.Cad.Commands.WritebackCommand))]

namespace DwgTranslator.Cad.Commands;

/// <summary>
/// AutoCAD command entry point for high-precision DWG writeback.
///
/// Communication mechanism: Fixed-path config file
/// - WPF app writes config to %TEMP%\DwgTranslator\writeback_config.json
/// - This command reads from that fixed path (no env vars, no command-line args)
/// - On completion, writes %TEMP%\DwgTranslator\writeback_done.txt as signal
/// - Deletes the config file on success
///
/// The config JSON contains: sourceDwgPath, outputDwgPath, entities[], cnToEn
/// </summary>
public class WritebackCommand
{
    /// <summary>
    /// Config file format for automated writeback.
    /// </summary>
    private class WritebackConfig
    {
        public string SourceDwgPath { get; set; } = "";
        public string OutputDwgPath { get; set; } = "";
        public List<TextEntity> Entities { get; set; } = new();
        public bool CnToEn { get; set; } = true;
    }

    /// <summary>
    /// Fixed path for inter-process communication via temp files.
    /// </summary>
    private static readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "DwgTranslator");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "writeback_config.json");
    private static readonly string DonePath = Path.Combine(ConfigDir, "writeback_done.txt");

    /// <summary>
    /// Executes the writeback operation within the AutoCAD process.
    /// Reads config from a fixed known path (written by the WPF app).
    /// </summary>
    [CommandMethod("DwgTranslateWrite", CommandFlags.Session)]
    public void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;

        // Wire logging to AutoCAD command line before engine runs.
        // All Log.Information/Warning/Error/Debug calls from the engine
        // will now appear on the user-visible command line.
        Cad.Log.Editor = ed;

        try
        {
            // Read config from fixed known path (no env vars, no command-line args needed)
            if (!File.Exists(ConfigPath))
            {
                ed.WriteMessage("\n没有找到回写配置文件。请先在 DWG Translator 中执行导出操作。");
                ed.WriteMessage($"\n预期配置文件位置: {ConfigPath}");
                SignalDone("no_config");
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<WritebackConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config == null || config.Entities == null || config.Entities.Count == 0)
            {
                ed.WriteMessage("\n配置文件无效或没有可回写的实体。");
                SignalDone("invalid_config");
                return;
            }

            if (string.IsNullOrEmpty(config.SourceDwgPath) || !File.Exists(config.SourceDwgPath))
            {
                ed.WriteMessage($"\n原始 DWG 文件不存在: {config.SourceDwgPath}");
                SignalDone("source_missing");
                return;
            }

            if (string.IsNullOrEmpty(config.OutputDwgPath))
            {
                ed.WriteMessage("\n输出路径为空。");
                SignalDone("no_output");
                return;
            }

            ed.WriteMessage($"\n开始回写 {config.Entities.Count} 个实体...");

            // Use side database on a temp COPY to avoid eFilerError
            // (can't ReadDwgFile on a file AutoCAD has open).
            // The active document is a blank Drawing1.dwg and does not contain
            // the source entities, so we must read the source file.
            var tempPath = Path.Combine(ConfigDir, $"__temp_{Path.GetFileName(config.SourceDwgPath)}");
            File.Copy(config.SourceDwgPath, tempPath, overwrite: true);

            DwgWriteResult result;
            try
            {
                var engine = new AcadWriterEngine();
                result = engine.WriteTranslations(
                    tempPath, config.OutputDwgPath,
                    config.Entities, config.CnToEn);
            }
            finally
            {
                // Clean up temp copy
                try { File.Delete(tempPath); } catch { }
            }

            if (result.SuccessCount > 0)
            {
                ed.WriteMessage($"\n回写完成: {result.SuccessCount} 成功, {result.FailCount} 失败。");

                // Delete config file on success (cleanup)
                try { File.Delete(ConfigPath); } catch { }
            }
            else
            {
                var errors = result.Errors.Count > 0
                    ? string.Join(", ", result.Errors.Take(3))
                    : "(无详细错误信息)";
                ed.WriteMessage($"\n回写失败: {errors}");
            }

            // Signal completion to WPF app
            SignalDone(result.SuccessCount > 0 ? "success" : "failed");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n命令执行错误: {ex.Message}");
            SignalDone("error");
        }
    }

    /// <summary>
    /// Writes a "done" signal file that the WPF app monitors for completion.
    /// </summary>
    private static void SignalDone(string status)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(DonePath, $"{status}|{DateTime.Now:O}");
        }
        catch { }
    }
}
