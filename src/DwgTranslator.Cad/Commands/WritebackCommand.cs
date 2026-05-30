using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Cad.Replacement;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Serilog;
using System.Text.Json;

namespace DwgTranslator.Cad.Commands;

/// <summary>
/// AutoCAD command entry point for high-precision DWG writeback.
/// Can be invoked via: (command "DwgTranslateWrite" "path/to/entities.json" "path/to/source.dwg" "path/to/output.dwg")
/// </summary>
public class WritebackCommand
{
    /// <summary>
    /// Executes the writeback operation within the AutoCAD process.
    /// Args: jsonFilePath sourceDwgPath outputDwgPath [cnToEn]
    /// </summary>
    [CommandMethod("DwgTranslateWrite", CommandFlags.Session)]
    public void Execute()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;

        try
        {
            // Prompt for JSON file containing translated entities
            var pr = ed.GetString("\n输入翻译实体 JSON 文件路径: ");
            if (pr.Status != PromptStatus.OK) return;
            string jsonPath = pr.StringResult.Trim('"');

            if (!File.Exists(jsonPath))
            {
                ed.WriteMessage($"\n文件不存在: {jsonPath}");
                return;
            }

            // Prompt for source DWG
            var prSrc = ed.GetString("\n输入原始 DWG 文件路径: ");
            if (prSrc.Status != PromptStatus.OK) return;
            string sourcePath = prSrc.StringResult.Trim('"');

            // Prompt for output DWG
            var prOut = ed.GetString("\n输入输出 DWG 文件路径: ");
            if (prOut.Status != PromptStatus.OK) return;
            string outputPath = prOut.StringResult.Trim('"');

            // Read entities from JSON
            var json = File.ReadAllText(jsonPath);
            var entities = JsonSerializer.Deserialize<List<TextEntity>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (entities == null || entities.Count == 0)
            {
                ed.WriteMessage("\n没有可回写的实体。");
                return;
            }

            ed.WriteMessage($"\n开始回写 {entities.Count} 个实体到 {outputPath} ...");

            var engine = new AcadWriterEngine();
            var result = engine.WriteTranslations(sourcePath, outputPath, entities, cnToEn: true);

            if (result.SuccessCount > 0)
            {
                ed.WriteMessage($"\n✅ 回写完成: {result.SuccessCount} 成功, {result.FailCount} 失败。");
            }
            else
            {
                ed.WriteMessage($"\n❌ 回写失败: {string.Join(", ", result.Errors.Take(3))}");
            }
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n命令执行错误: {ex.Message}");
            Log.Error(ex, "WritebackCommand failed");
        }
    }
}
