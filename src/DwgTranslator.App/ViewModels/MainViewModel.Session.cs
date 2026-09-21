using DwgTranslator.Core.Services;
using System.IO;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// 会话恢复：把上次的工作区摆回来（语言方向 + 上次打开的图纸列表），启动时不再面对空工作区。
///
/// 为什么不直接用任务记录（tasks.json）：任务记录只覆盖"跑过的图纸"，还会被
/// 「清除未完成记录」等操作改写；工作区是用户的界面状态，跟任务生命周期无关。
/// 这份记录是便利缓存（workspace-session.json，按账号存放），读不出来只是退化成空工作区，
/// 不影响任何功能，也不需要用户做任何决策。
/// </summary>
public partial class MainViewModel
{
    private const string WorkspaceSessionFileName = "workspace-session.json";

    private CancellationTokenSource? _workspaceSessionSaveCts;
    /// <summary>恢复过程本身会改 DrawingFiles；这些改动不需要再回写一份一模一样的记录。</summary>
    private bool _restoringWorkspace;

    private WorkspaceSessionStore SessionStore => new(Path.Combine(AccountDataDirectory, WorkspaceSessionFileName));

    /// <summary>
    /// 启动时恢复上次工作区。任何一步失败都只退化成空工作区，绝不阻断启动。
    /// </summary>
    private void RestoreLastWorkspace()
    {
        if (!_config.RestoreLastWorkspace)
        {
            // 关掉恢复的用户不需要这份记录：顺手清掉，不把上次的图纸路径留在盘上。
            SessionStore.Clear();
            Log.Information("已在设置里关闭会话恢复，本次启动使用空工作区");
            return;
        }
        // 命令行/"打开方式"传进来的图纸优先级最高：用户已经明确说要打开什么。
        if (App.StartupFiles.Length > 0)
        {
            Log.Information("命令行已指定 {Count} 个图纸，不再恢复上次工作区", App.StartupFiles.Length);
            return;
        }
        // 安装后的环境自检（--env-check）是安装流程的一部分，不该顺带加载上次的图纸。
        if (App.OpenEnvironmentCheckOnStart)
        {
            Log.Information("本次为环境自检启动，未恢复上次工作区");
            return;
        }
        // 校对记录或未完成任务已经把工作区填好了：叠加会话记录只会让人分不清哪些是刚恢复的。
        if (DrawingFiles.Count > 0)
        {
            Log.Information("工作区已由校对记录或未完成任务恢复（{Count} 行），不再叠加会话记录", DrawingFiles.Count);
            return;
        }

        var snapshot = SessionStore.Read();
        if (snapshot == null) return;

        RestoreLanguagePairIfConfigFailed(snapshot);

        var available = new List<string>();
        var missing = new List<string>();
        foreach (var path in snapshot.Drawings)
        {
            // 图纸被移动、改名、换盘符是常态：检查失败只算"不在"，绝不抛异常，也不弹窗拦启动。
            try
            {
                if (File.Exists(path)) available.Add(path); else missing.Add(Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                missing.Add(Path.GetFileName(path));
                Log.Debug(ex, "检查上次工作区的图纸是否存在失败：{Path}", path);
            }
        }
        if (missing.Count > 0)
            Log.Information("上次工作区有 {Count} 张图纸已不在原路径，未恢复：{Files}", missing.Count, string.Join("; ", missing));
        if (available.Count == 0)
        {
            if (missing.Count > 0) StatusMessage = $"上次打开的 {missing.Count} 张图纸已移动或删除，工作区为空。";
            return;
        }

        _restoringWorkspace = true;
        try
        {
            RebuildDrawingFileList(available);
            // 有任务记录的图纸保持记录里的状态（已完成的别显示成"待处理"）；完全没有任务记录的
            // 才入队——否则"开始翻译"会因为队列为空直接拒绝，恢复出来的图纸等于点不动。
            foreach (var path in available)
            {
                if (HasTaskFor(path)) continue;
                try { _taskManager.Enqueue(path); }
                catch (Exception ex) { Log.Warning(ex, "恢复工作区时入队失败：{File}", Path.GetFileName(path)); }
            }
            AttachTasksToRows();
        }
        finally { _restoringWorkspace = false; }

        StatusMessage = missing.Count == 0
            ? $"已恢复上次工作区：{available.Count} 张图纸 · {LanguageDirection}"
            : $"已恢复上次工作区 {available.Count} 张图纸；另有 {missing.Count} 张已移动或删除，未恢复。";
        Log.Information("已恢复上次工作区：{Restored} 张图纸（{Missing} 张缺失），方向 {Direction}",
            available.Count, missing.Count, LanguageDirection);
    }

    private bool HasTaskFor(string path)
    {
        var normalized = NormalizeSourcePath(path);
        return _taskManager.Tasks.Any(task =>
            string.Equals(NormalizeSourcePath(task.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 语言方向本来就由 settings.json 持久化（LoadConfig → ApplyLanguagePair），这里是兜底：
    /// 设置文件读坏时 _config 会退回默认值（ZH→EN），用户上次选的方向就丢了；此时用会话记录里的
    /// 那一对补回来。设置文件读成功时绝不动它——用户在设置里明确选过的方向优先。
    /// </summary>
    private void RestoreLanguagePairIfConfigFailed(WorkspaceSessionStore.Snapshot snapshot)
    {
        if (!_configLoadFailed) return;
        if (string.IsNullOrWhiteSpace(snapshot.SourceLanguage) || string.IsNullOrWhiteSpace(snapshot.TargetLanguage)) return;
        ApplyLanguagePair(snapshot.SourceLanguage, snapshot.TargetLanguage);
        Log.Information("设置文件未能读出语言方向，已用上次工作区的方向兜底：{Direction}", LanguageDirection);
    }

    /// <summary>
    /// 工作区变化后的延迟保存：导入/删除图纸会连着改集合，合并成一次写盘（与项目自动保存同一套防抖）。
    /// </summary>
    private async void ScheduleWorkspaceSessionSave()
    {
        if (_restoringWorkspace) return;
        _workspaceSessionSaveCts?.Cancel();
        _workspaceSessionSaveCts?.Dispose();
        var cts = _workspaceSessionSaveCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(1200, cts.Token);
            if (!cts.IsCancellationRequested) SaveWorkspaceSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "保存上次工作区记录失败"); }
    }

    /// <summary>
    /// 落盘"上次工作区"。只在启用恢复时写；关掉恢复的用户不需要这份记录，也不该被它占磁盘。
    /// 写失败只记日志：一份便利缓存不能反过来影响用户正在做的事。
    /// </summary>
    public void SaveWorkspaceSession()
    {
        try
        {
            if (!_config.RestoreLastWorkspace) return;
            SessionStore.Save(new WorkspaceSessionStore.Snapshot
            {
                SourceLanguage = CurrentSourceLang,
                TargetLanguage = CurrentTargetLang,
                Drawings = DrawingFiles
                    .Select(row => row.FullPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList()
            });
        }
        catch (Exception ex) { Log.Warning(ex, "保存上次工作区记录失败"); }
    }
}
