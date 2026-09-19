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

        var edited = _proofreadingOriginals.Keys.ToArray();
        var statusesBeforeCommit = edited.ToDictionary(entity => entity, entity => entity.Status);
        var affectedSources = edited
            .Select(entity => NormalizeSourcePath(entity.SourceFilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            // The durable record must contain the post-review status. Keep a short-lived snapshot so
            // a locked file, project conflict or disk error restores exactly what the user was editing.
            foreach (var entity in edited)
            {
                entity.Status = string.IsNullOrWhiteSpace(entity.TranslatedText)
                    ? TranslationStatus.Pending
                    : TranslationStatus.Reviewed;
            }

            if (ActiveTranslationProject != null)
            {
                if (!SaveActiveProject())
                {
                    RestoreProofreadingCommitStatuses(statusesBeforeCommit);
                    return false;
                }
            }
            else
            {
                ProofreadingStore.Save(Entities.ToArray(), edited);
            }

            _proofreadingWorkspaceVersion++;
            _proofreadingOriginals.Clear();
            OnPropertyChanged(nameof(HasUnsavedProofreading));
            UpdateStatistics();
            ApplyFilter();

            var completedTasks = MarkReviewedTasksCompleted(affectedSources);
            StatusMessage = completedTasks > 0
                ? $"校对更改已保存，{completedTasks} 张图纸已进入待导出；请显式导出以生成输出图纸。"
                : "校对更改已保存到当前账号；仍有未校对、空译文或失败条目时，任务会继续保留在待校对。";
            RaiseWorkspaceSummaryProperties();
            return true;
        }
        catch (Exception ex)
        {
            RestoreProofreadingCommitStatuses(statusesBeforeCommit);
            Log.Warning(ex, "校对记录保存失败；保留当前编辑");
            StatusMessage = "校对保存失败，更改仍保留且未离开当前工作区。请检查图纸、磁盘空间和目录权限后重试。";
            return false;
        }
    }

    private static void RestoreProofreadingCommitStatuses(
        IReadOnlyDictionary<TextEntity, TranslationStatus> statusesBeforeCommit)
    {
        foreach (var pair in statusesBeforeCommit)
            pair.Key.Status = pair.Value;
    }

    private int MarkReviewedTasksCompleted(IEnumerable<string> affectedSources)
    {
        var completed = 0;
        foreach (var source in affectedSources)
        {
            var sourceEntities = Entities.Where(entity => string.Equals(
                NormalizeSourcePath(entity.SourceFilePath), source, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sourceEntities.Length == 0 || sourceEntities.Any(entity => entity.Status is
                TranslationStatus.Pending or TranslationStatus.Translated or TranslationStatus.GlossaryMatched
                or TranslationStatus.TranslationFailed or TranslationStatus.WritebackFailed))
                continue;

            var row = DrawingFiles.FirstOrDefault(item => string.Equals(
                NormalizeSourcePath(item.FullPath), source, StringComparison.OrdinalIgnoreCase));
            var task = row?.Task;
            if (task?.Status != DwgTranslator.Core.Tasks.TranslationTaskStatus.ReadyForReview)
                continue;

            try
            {
                _taskManager.MarkReviewCompleted(task.Id);
                completed++;
            }
            catch (Exception ex)
            {
                // The proofreading data is already durable. Do not pretend the save failed; retain the
                // task in ReadyForReview and make the discrepancy visible in diagnostics for recovery.
                Log.Warning(ex, "校对已保存，但任务状态推进失败 {TaskId}", task.Id);
            }
        }
        return completed;
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
            MigrateLegacyProofreading(restored.Entities);
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
