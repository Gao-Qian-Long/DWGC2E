using CommunityToolkit.Mvvm.Input;
using System.IO;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Resources;
using DwgTranslator.Core.Tasks;
using System.Windows;
using Serilog;

namespace DwgTranslator.App.ViewModels;

/// <summary>
/// 任务层接入（重构计划里"唯一剩余工作项"）：图纸翻译页的主链路从
/// <c>多文件串行 for 循环 + UI 自己 new TranslationService 直连 AI</c>
/// 迁到 <see cref="ITaskManager"/>。
///
/// 分工从此固定下来：
///   · UI —— 只负责"提交哪些图纸、显示什么状态、用户点了什么按钮"；
///   · 任务层 —— 负责解析 / 翻译 / 排版 / 写回，以及两层并发限流与失败隔离；
///   · 事件 —— TaskUpdated（行状态）、ProgressMessage（阶段日志）、OverallProgressChanged（总进度）、
///     TranslationCompleted（同步文字条目表，人工校对与 Excel 复核仍要用实体列表）。
///
/// 任务层的事件都在工作线程上触发，所以这里统一用 <see cref="OnUiThread"/> 回 UI 线程，
/// 保证 ObservableCollection / 绑定属性只在 UI 线程被改写。
/// </summary>
public partial class MainViewModel
{
    public string TaskRecoveryWarning => (_taskManager as ITaskRecoveryDiagnostics)?.RecoveryWarning ?? string.Empty;
    public bool HasTaskRecoveryWarning => !string.IsNullOrEmpty(TaskRecoveryWarning);

    /// <summary>
    /// 上次运行遗留的未完成任务数。启动不再弹模态框询问，改用导航红点 + 任务中心的提示条呈现，
    /// 让"有东西没跑完"可见但不打断启动。
    /// </summary>
    public int PendingTaskCount => _taskManager.PendingFromLastRun.Count;
    public bool HasPendingTasks => PendingTaskCount > 0;
    public string PendingTasksNotice
    {
        get
        {
            var pending = _taskManager.PendingFromLastRun;
            if (pending.Count == 0) return string.Empty;
            var names = string.Join("、", pending.Take(5).Select(t => t.FileName));
            return pending.Count > 5
                ? $"上次有 {pending.Count} 张图纸没有跑完：{names} 等 {pending.Count} 张"
                : $"上次有 {pending.Count} 张图纸没有跑完：{names}";
        }
    }

    private void RefreshTaskRecoveryNotice()
    {
        OnPropertyChanged(nameof(TaskRecoveryWarning));
        OnPropertyChanged(nameof(HasTaskRecoveryWarning));
        OnPropertyChanged(nameof(PendingTaskCount));
        OnPropertyChanged(nameof(HasPendingTasks));
        OnPropertyChanged(nameof(PendingTasksNotice));
    }

    /// <summary>任务中心的「继续处理」：沿用上次的写回方式直接续跑队列。</summary>
    [RelayCommand]
    private Task ResumePendingTasksAsync()
    {
        if (!HasPendingTasks || _taskManager.IsRunning) return Task.CompletedTask;
        return RunTaskQueueAsync(retryFailedFirst: false, askWritebackMode: false);
    }

    /// <summary>任务中心的「清除未完成记录」：只删记录，不删图纸文件，也不动已完成/已取消的历史。</summary>
    [RelayCommand]
    private void ClearPendingTasks()
    {
        var pending = _taskManager.PendingFromLastRun;
        if (pending.Count == 0) return;
        if (Views.PromptDialog.Show(
                $"将清除 {pending.Count} 条未完成记录。\n\n图纸文件本身不会被删除，再次导入仍可翻译；已完成或已取消的历史记录不受影响。是否继续？",
                "清除未完成记录", MessageBoxButton.YesNo,
                confirmText: "清除记录", rejectText: "保留记录") != MessageBoxResult.Yes) return;
        foreach (var task in pending.ToArray()) _taskManager.Remove(task);
        StatusMessage = Strings.Get("StatusReady");
        RefreshTaskRecoveryNotice();
    }

    #region 事件订阅

    // The task manager and MainViewModel are both singletons now, but the handlers are still stored
    // and detached on window close so a disposed/recreated UI never keeps stale subscriptions alive.
    private EventHandler<TranslationTask>? _taskUpdatedHandler;
    private EventHandler<string>? _taskProgressHandler;
    private EventHandler<double>? _taskOverallProgressHandler;
    private EventHandler<TranslationPair>? _taskTranslationCompletedHandler;

    private void SubscribeTaskEvents()
    {
        _taskUpdatedHandler ??= (_, task) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskUpdated(task); }); };
        _taskProgressHandler ??= (_, message) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskProgressMessage(message); }); };
        _taskOverallProgressHandler ??= (_, percent) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskOverallProgress(percent); }); };
        _taskTranslationCompletedHandler ??= (_, pair) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnEntityTranslated(pair); }); };
        // Detach before attaching so a repeated call can never subscribe twice.
        _taskManager.TaskUpdated -= _taskUpdatedHandler;
        _taskManager.TaskUpdated += _taskUpdatedHandler;
        _taskManager.ProgressMessage -= _taskProgressHandler;
        _taskManager.ProgressMessage += _taskProgressHandler;
        _taskManager.OverallProgressChanged -= _taskOverallProgressHandler;
        _taskManager.OverallProgressChanged += _taskOverallProgressHandler;
        _taskManager.TranslationCompleted -= _taskTranslationCompletedHandler;
        _taskManager.TranslationCompleted += _taskTranslationCompletedHandler;
    }

    /// <summary>Releases the singleton task manager's references to this ViewModel.</summary>
    private void UnsubscribeTaskEvents()
    {
        if (_taskUpdatedHandler != null) _taskManager.TaskUpdated -= _taskUpdatedHandler;
        if (_taskProgressHandler != null) _taskManager.ProgressMessage -= _taskProgressHandler;
        if (_taskOverallProgressHandler != null) _taskManager.OverallProgressChanged -= _taskOverallProgressHandler;
        if (_taskTranslationCompletedHandler != null) _taskManager.TranslationCompleted -= _taskTranslationCompletedHandler;
    }

    private void OnTaskUpdated(TranslationTask task)
    {
        // 优先按任务 Id 找行：同一张图纸重跑后是新任务，路径相同的旧行不该抢走新任务的进度。
        var row = DrawingFiles.FirstOrDefault(r => string.Equals(r.Task?.Id, task.Id, StringComparison.Ordinal))
            ?? DrawingFiles.FirstOrDefault(r => string.Equals(
                NormalizeSourcePath(r.FullPath), NormalizeSourcePath(task.FilePath), StringComparison.OrdinalIgnoreCase));

        if (row == null)
        {
            // 启动时恢复的任务：还没有对应的导入行，补一行出来（实体表可以稍后再看）。
            row = new DrawingFileItem(task.FilePath) { Index = DrawingFiles.Count + 1 };
            DrawingFiles.Add(row);
            HasDrawingFiles = true;
            HasMultipleDrawingFiles = DrawingFiles.Count > 1;
        }

        // AttachTask 会刷新约 20 个绑定属性；按行 100ms 节流，终态（IsFinished）立即刷新不节流。
        var nowUtc = DateTime.UtcNow;
        if (task.IsFinished || nowUtc - _lastRowAttachUtc.GetValueOrDefault(row, DateTime.MinValue) >= TimeSpan.FromMilliseconds(100))
        {
            _lastRowAttachUtc[row] = nowUtc;
            row.AttachTask(task);
        }

        // 状态栏不必每条进度都改（一张图纸上千条），500ms 一次既跟得上又不刷屏。
        if (!task.IsFinished && DateTime.UtcNow - _lastTaskStatusUtc < TimeSpan.FromMilliseconds(500)) return;
        _lastTaskStatusUtc = DateTime.UtcNow;
        StatusMessage = task.IsFinished
            ? $"{task.FileName}：{task.StatusText}" + (string.IsNullOrWhiteSpace(task.Error) ? string.Empty : $" —— {task.Error}")
            : $"{task.FileName}：{task.StatusText} {task.Progress:0}%";
    }

    private DateTime _lastTaskStatusUtc = DateTime.MinValue;

    /// <summary>每行的上次 AttachTask 时间（UI 侧节流）。</summary>
    private readonly Dictionary<DrawingFileItem, DateTime> _lastRowAttachUtc = new();

    /// <summary>阶段化日志：[1/6] 总装配图.dwg 正在解析图纸……任务层已同时写进 Serilog 日志。</summary>
    private void OnTaskProgressMessage(string message)
    {
        ProgressDetailText = message;
        Log.Information("{TaskProgress}", message);
    }

    private void OnTaskOverallProgress(double percent)
    {
        ProgressValue = percent;
        UpdateEta(percent);
    }

    /// <summary>
    /// 单条译文回填到实体列表（按 句柄 + 来源图纸 匹配，句柄只在单张图纸内唯一）。
    /// 这一步保证「文字条目表」与任务层看到同一份结果：用户点"条目"进去校对时，
    /// 不会出现"任务显示已完成、条目全待翻译"的错位。
    /// </summary>
    private void OnEntityTranslated(TranslationPair pair)
    {
        if (pair == null) return;

        var key = (pair.Handle?.ToUpperInvariant() ?? string.Empty,
                   NormalizeSourcePath(pair.SourceFilePath).ToUpperInvariant());

        if (!EntityIndex.TryGetValue(key, out var matching) || matching.Count == 0) return;

        foreach (var entity in matching)
        {
            entity.TranslatedText = pair.TranslatedText;
            entity.GlossaryHit = pair.GlossaryHit;
            entity.Status = pair.Status;
        }

        // 与旧流程口径一致：按"唯一文本"计数，而不是按实体条数（重复文本只算一次）。
        if (pair.Status == TranslationStatus.TranslationFailed) FailedCount++;
        else TranslatedCount++;
        if (pair.GlossaryHit) GlossaryHitCount++;
        CacheHitCount = _consistencyService.CacheSize;
    }

    #endregion

    #region 实体索引（进度回调用，避免每条译文都全表扫描）

    private Dictionary<(string Handle, string Path), List<TextEntity>>? _entityIndex;

    private Dictionary<(string Handle, string Path), List<TextEntity>> EntityIndex =>
        _entityIndex ??= BuildEntityIndex();

    private Dictionary<(string Handle, string Path), List<TextEntity>> BuildEntityIndex()
    {
        var index = new Dictionary<(string, string), List<TextEntity>>(Entities.Count);
        foreach (var entity in Entities)
        {
            var key = (entity.Handle?.ToUpperInvariant() ?? string.Empty,
                       NormalizeSourcePath(entity.SourceFilePath).ToUpperInvariant());
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<TextEntity>();
                index[key] = list;
            }
            list.Add(entity);
        }
        return index;
    }

    /// <summary>实体集合发生变化（导入 / 清空）后必须调用，否则索引指向上一批对象。</summary>
    private void InvalidateEntityIndex() => _entityIndex = null;

    #endregion

    #region 队列执行

    /// <summary>
    /// 「开始翻译」/「重试失败」/「继续未完成任务」共用的一条入口。
    /// </summary>
    /// <param name="retryFailedFirst">true = 先把失败任务重置回等待中再跑（重试失败按钮）。</param>
    /// <param name="askWritebackMode">是否先问写回方式（离线 / AutoCAD）；恢复续跑时沿用上次选择。</param>
    [RelayCommand]
    private async Task RetryDrawingAsync(DrawingFileItem? item)
    {
        if (item == null || IsProcessing) return;
        var task = _taskManager.Tasks.FirstOrDefault(t => string.Equals(t.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        if (task == null) return;
        await RunTaskQueueAsync(false, false, task.Id);
    }

    private async Task RunTaskQueueAsync(bool retryFailedFirst, bool askWritebackMode = true, string? onlyTaskId = null)
    {
        if (!RequireAccount()) return;
        if (IsGlossaryLoading) { StatusMessage = "正在加载术语，请稍候再开始任务。"; return; }
        if (!IsProcessing) ApplySavedSettings();
        if (IsProcessing) return;

        if (_config.LicensingEnabled && !_licenseService.CanExecuteOperation())
        {
            StatusMessage = Strings.Get("StatusLicenseRequired");
            PromptForActivation();
            return;
        }

        var workerMode = string.Equals(_apiClient.ModeName, "worker", StringComparison.OrdinalIgnoreCase);
        if (workerMode && !_apiClient.IsConfigured)
        {
            StatusMessage = "服务配置异常，请联系管理员修复安装配置。";
            DwgTranslator.App.Views.PromptDialog.Show(StatusMessage, Strings.Get("MsgTitleConfigError"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Worker mode uses a server session, never the user's DeepSeek API key.
        // Server-side expiry, subscription and quota checks remain authoritative.
        if (workerMode && string.IsNullOrWhiteSpace(AppConfig.DecryptApiKey(_config.AuthTokenEncrypted)))
        {
            StatusMessage = "请先登录后再开始翻译。";
            DwgTranslator.App.Views.PromptDialog.Show(StatusMessage, Strings.Get("MsgTitleConfigError"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var tasks = _taskManager.Tasks;
        if (tasks.Count == 0)
        {
            StatusMessage = "队列里还没有图纸，请先添加 DWG / DXF。";
            return;
        }

        var runnable = onlyTaskId != null ? tasks.Where(t => t.Id == onlyTaskId).ToList() : retryFailedFirst
            ? tasks.Where(t => t.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted).ToList()
            : tasks.Where(t => t.Status == TranslationTaskStatus.Pending
                || t.Status == TranslationTaskStatus.Paused).ToList();

        if (runnable.Count == 0)
        {
            StatusMessage = retryFailedFirst ? Strings.Get("StatusNoFailedRetry") : "队列里没有待处理的图纸。";
            return;
        }

        // 翻译阶段只解析、提取并生成译文；输出目录与写回模式在“回写并导出”时选择。
        _taskManager.ConfigureRun(CurrentSourceLang, CurrentTargetLang);

        IsProcessing = true;
        IsTranslating = true;
        IsCancellationRequested = false;
        OperationLabel = Strings.Get("OperationTranslating");
        MarkOperationStarted();
        ProgressValue = 0;
        _cts = new CancellationTokenSource();

        try
        {
            StatusMessage = retryFailedFirst
                ? $"正在重试 {runnable.Count} 张失败的图纸…"
                : $"队列开始：{runnable.Count} 张图纸（本地并发 {_taskManager.Options.LocalWorkerCount}，单图纸 AI 并发 {_taskManager.Options.AiConcurrency}）";

            Task runTask = onlyTaskId != null
                ? _taskManager.RetryTaskAsync(onlyTaskId, _cts.Token)
                : retryFailedFirst
                    ? _taskManager.RetryFailedAsync(_cts.Token)
                    : _taskManager.RunAsync(_cts.Token);

            // RunAsync consumes any recovered-task markers synchronously before its first await.
            // Refresh immediately so the navigation red dot/banner disappear as soon as the user
            // chooses to continue, rather than lingering until another unrelated workspace change.
            RefreshTaskRecoveryNotice();
            await runTask.ConfigureAwait(true);

            ArchiveTranslationRun(runnable);
            StatusMessage = BuildQueueSummary();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.Get("StatusTranslateCancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Task queue failed");
            StatusMessage = Strings.Get("StatusTranslateFailed");
            DwgTranslator.App.Views.PromptDialog.Show(Strings.Get("MsgTranslateError"), Strings.Get("MsgTitleError"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
            ApplySavedSettings();
            IsTranslating = false;
            IsIndeterminate = false;
            OperationLabel = string.Empty;
            IsCancellationRequested = false;
            ProgressDetailText = string.Empty;

            UpdateStatistics();
            ApplyFilter();
            RefreshPageStatistics();
            RefreshTaskRecoveryNotice();
        }
    }

    /// <summary>队列结束后的结果概览（成功 / 失败 / 取消分开说，不用"操作完成"糊过去）。</summary>
    private string BuildQueueSummary()
    {
        var tasks = _taskManager.Tasks;
        int ready = tasks.Count(t => t.Status == TranslationTaskStatus.ReadyForReview);
        int completed = tasks.Count(t => t.Status == TranslationTaskStatus.Completed);
        int failed = tasks.Count(t => t.Status == TranslationTaskStatus.Failed);
        int partial = tasks.Count(t => t.Status == TranslationTaskStatus.PartiallyCompleted);
        int skipped = tasks.Count(t => t.Status == TranslationTaskStatus.Skipped);
        int cancelled = tasks.Count(t => t.Status == TranslationTaskStatus.Cancelled);
        int pending = tasks.Count(t => t.Status is TranslationTaskStatus.Pending or TranslationTaskStatus.Paused);

        // 翻译完成与已校对现在都是"待导出"（2026-09-22 校对降级为可选），合并成一个计数，
        // 不再向用户报一个他既看不懂、又会以为"必须先处理"的"待校对"。
        var summary = $"队列结束：待导出 {ready + completed} 张，失败 {failed} 张";
        if (partial > 0) summary += $"，部分完成 {partial} 张";
        if (skipped > 0) summary += $"，跳过 {skipped} 张";
        if (cancelled > 0) summary += $"，取消 {cancelled} 张";
        if (pending > 0) summary += $"，待处理 {pending} 张";

        return summary;
    }

    /// <summary>「停止任务」：取消当前队列，已完成的记录保留。</summary>
    private void CancelTaskQueue()
    {
        IsCancellationRequested = true;
        _taskManager.CancelCurrentRun();
        StatusMessage = Strings.Get("StatusStoppingTranslation");
    }

    #endregion

    #region 行 ↔ 任务对应

    /// <summary>导入后把每一行挂到它最新的任务上（没有任务的行保持"未入队"外观）。</summary>
    private void AttachTasksToRows()
    {
        var tasks = _taskManager.Tasks;
        if (tasks.Count == 0) return;

        foreach (var row in DrawingFiles)
        {
            var rowPath = NormalizeSourcePath(row.FullPath);
            var task = tasks
                .Where(t => string.Equals(NormalizeSourcePath(t.FilePath), rowPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefault();

            if (task != null) row.AttachTask(task);
        }
    }

    /// <summary>
    /// 启动时把上次未完成的任务放回队列（任务层已从 tasks.json 恢复）。
    /// 这里不再弹窗：任务只以"等待中"显示，并由导航红点与任务中心提示条告知，
    /// 是否续跑、是否清除都由用户在任务中心里显式决定，启动过程不被打断。
    /// </summary>
    private void ResumePendingTasks()
    {
        var pending = _taskManager.PendingFromLastRun;
        // TaskManager persists completed/failed history as well as interrupted work. The
        // task-center table is workspace-backed, so restore one visible row for every saved
        // drawing instead of only adding the interrupted subset.
        foreach (var task in _taskManager.Tasks.OrderByDescending(item => item.CreatedAt))
        {
            if (DrawingFiles.All(r => !string.Equals(
                    NormalizeSourcePath(r.FullPath), NormalizeSourcePath(task.FilePath),
                    StringComparison.OrdinalIgnoreCase)))
            {
                DrawingFiles.Add(new DrawingFileItem(task.FilePath) { Index = DrawingFiles.Count + 1 });
            }
        }
        AttachTasksToRows();
        HasDrawingFiles = DrawingFiles.Count > 0;
        HasMultipleDrawingFiles = DrawingFiles.Count > 1;
        if (pending.Count > 0)
        {
            Log.Information("上次遗留 {Count} 个未完成任务，已用红点提示并由任务中心处理", pending.Count);
        }

        RefreshTaskRecoveryNotice();
    }

    [RelayCommand]
    private async Task RetranslateSkippedChineseAsync(DrawingFileItem? row)
    {
        var task = row?.Task;
        if (row == null || task == null || !row.HasSkippedChinese || IsProcessing) return;

        // Historical tasks may have a different language pair from the current workspace.
        if (!string.IsNullOrWhiteSpace(task.ProjectId))
        {
            try
            {
                var project = ProjectStore.Load(task.ProjectId);
                CurrentSourceLang = project.SourceLanguage;
                CurrentTargetLang = project.TargetLanguage;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取漏译补翻任务的语言设置失败：{TaskId}", task.Id);
                StatusMessage = "无法读取该历史任务的语言方向，请先从翻译项目中打开它。";
                return;
            }
        }

        var count = row.SkippedChineseCount;
        StatusMessage = $"正在重新检查 {count} 条旧检查点中被跳过的中文；已完成译文会继续复用。";
        await RunTaskQueueAsync(retryFailedFirst: false, askWritebackMode: false, onlyTaskId: task.Id);
    }

    #endregion

    #region UI 线程调度

    /// <summary>
    /// 任务层事件回到 UI 线程。取不到 Dispatcher（单元测试 / 设计器）时直接执行，
    /// 这样 ViewModel 在没有 WPF 消息循环的环境里也能被驱动。
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action);
    }

    #endregion
}
