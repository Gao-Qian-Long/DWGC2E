using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System.IO;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    private int _proofreadingWorkspaceVersion;
    private ProofreadingStore ProofreadingStore => new(Path.Combine(AccountDataDirectory, "proofreading.json"));

    private bool TrySaveProofreading()
    {
        if (!HasUnsavedProofreading) return true;
        if (IsProcessing || IsExporting || _taskManager.IsRunning)
        { StatusMessage = "请等待当前任务结束后保存校对。"; return false; }
        try
        {
            // Commit first. A failed save must retain both original values and the dirty marker.
            ProofreadingStore.Save(Entities.ToArray(), _proofreadingOriginals.Keys.ToArray());
            foreach (var entity in _proofreadingOriginals.Keys) MarkTranslationEdited(entity);
            _proofreadingWorkspaceVersion++;
            _proofreadingOriginals.Clear();
            OnPropertyChanged(nameof(HasUnsavedProofreading));
            StatusMessage = "校对更改已保存到当前账号，下次启动可恢复；请显式导出以生成输出图纸。";
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "校对记录保存失败；保留当前编辑");
            StatusMessage = "校对保存失败，更改仍保留且未离开当前工作区。请检查图纸、磁盘空间和目录权限后重试。";
            return false;
        }
    }

    private bool TryClearSavedProofreading()
    {
        try { ProofreadingStore.Clear(); _proofreadingWorkspaceVersion++; return true; }
        catch (Exception ex)
        {
            Log.Warning(ex, "无法清除校对保存记录；保留当前工作区");
            StatusMessage = "校对保存记录无法清除，已保留当前工作区。请检查文件权限后重试。";
            return false;
        }
    }

    private async Task RestoreSavedProofreadingAsync()
    {
        if (Entities.Count != 0 || HasUnsavedProofreading || _taskManager.IsRunning || IsExporting) return;
        var version = _sessionVersion;
        var workspaceVersion = ++_proofreadingWorkspaceVersion;
        var store = ProofreadingStore;
        var dxfReader = App.Services?.GetService<IDxfReaderService>();
        try
        {
            var restored = await Task.Run(() => store.Restore(path =>
                string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase)
                    ? dxfReader?.ExtractFromFile(path) ?? throw new IOException("DXF读取器不可用。")
                    : _dwgReaderService.ExtractFromFile(path)));
            // Ignore a stale async result after navigation, import, account switch or task start.
            if (version != _sessionVersion || workspaceVersion != _proofreadingWorkspaceVersion || Entities.Count != 0 || HasUnsavedProofreading || IsProcessing || IsExporting || _taskManager.IsRunning) return;
            foreach (var entity in restored.Entities) Entities.Add(entity);
            InvalidateEntityIndex();
            if (restored.Sources.Count > 0)
            {
                var sources = DrawingFiles.Select(r => r.FullPath).Concat(restored.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                RebuildDrawingFileList(sources);
                AttachTasksToRows();
                ApplyFilter(); UpdateStatistics();
            }
            if (restored.SkippedSources.Count > 0)
                StatusMessage = $"已恢复 {restored.Entities.Count} 条已保存校对；{restored.SkippedSources.Count} 张图纸已变化、缺失或无法匹配，未套用旧校对。原记录仍保留。";
            else if (restored.Entities.Count > 0)
                StatusMessage = $"已恢复当前账号 {restored.Entities.Count} 条已保存校对；输出图纸仍需显式导出。";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "校对记录恢复失败，原文件保留");
            StatusMessage = "校对保存记录暂时无法恢复，原记录已保留。请检查图纸和记录文件。";
        }
    }
}
