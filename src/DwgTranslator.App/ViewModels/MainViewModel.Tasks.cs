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
    #region 事件订阅

    /// <summary>构造函数里调用一次；Dispose 时（进程退出）不需要单独退订。</summary>
    private void SubscribeTaskEvents()
    {
        _taskManager.TaskUpdated += (_, task) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskUpdated(task); }); };
        _taskManager.ProgressMessage += (_, message) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskProgressMessage(message); }); };
        _taskManager.OverallProgressChanged += (_, percent) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnTaskOverallProgress(percent); }); };
        _taskManager.TranslationCompleted += (_, pair) => { var version = _sessionVersion; OnUiThread(() => { if (version == _sessionVersion) OnEntityTranslated(pair); }); };
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

        row.AttachTask(task);

        // 状态栏不必每条进度都改（一张图纸上千条），500ms 一次既跟得上又不刷屏。
        if (DateTime.UtcNow - _lastTaskStatusUtc < TimeSpan.FromMilliseconds(500)) return;
        _lastTaskStatusUtc = DateTime.UtcNow;
        StatusMessage = task.IsFinished
            ? $"{task.FileName}：{task.StatusText}" + (string.IsNullOrWhiteSpace(task.Error) ? string.Empty : $" —— {task.Error}")
            : $"{task.FileName}：{task.StatusText} {task.Progress:0}%";
    }

    private DateTime _lastTaskStatusUtc = DateTime.MinValue;

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
    private async Task RunTaskQueueAsync(bool retryFailedFirst, bool askWritebackMode = true)
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

        var runnable = retryFailedFirst
            ? tasks.Where(t => t.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted).ToList()
            : tasks.Where(t => t.Status == TranslationTaskStatus.Pending
                || t.Status == TranslationTaskStatus.Paused).ToList();

        if (runnable.Count == 0)
        {
            StatusMessage = retryFailedFirst ? Strings.Get("StatusNoFailedRetry") : "队列里没有待处理的图纸。";
            return;
        }

        TaskWritebackMode writebackMode = _taskManager.WritebackMode;
        if (askWritebackMode)
        {
            var modeDialog = new Views.ExportModeDialog(_autoCadInteropService.IsAutoCADAvailable(_config))
            {
                Owner = Application.Current?.MainWindow
            };
            if (modeDialog.ShowDialog() != true)
            {
                StatusMessage = Strings.Get("StatusExportCancelled");
                return;
            }
            writebackMode = modeDialog.SelectedMode == Views.ExportModeDialog.ExportMode.AutoCAD
                ? TaskWritebackMode.AutoCad
                : TaskWritebackMode.Offline;
        }

        _taskManager.ConfigureRun(CurrentSourceLang, CurrentTargetLang, _config.ExportDirectory, writebackMode);

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

            if (retryFailedFirst)
                await _taskManager.RetryFailedAsync(_cts.Token).ConfigureAwait(true);
            else
                await _taskManager.RunAsync(_cts.Token).ConfigureAwait(true);

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
        }
    }

    /// <summary>队列结束后的结果概览（成功 / 失败 / 取消分开说，不用"操作完成"糊过去）。</summary>
    private string BuildQueueSummary()
    {
        var tasks = _taskManager.Tasks;
        int completed = tasks.Count(t => t.Status == TranslationTaskStatus.Completed);
        int failed = tasks.Count(t => t.Status == TranslationTaskStatus.Failed);
        int partial = tasks.Count(t => t.Status == TranslationTaskStatus.PartiallyCompleted);
        int skipped = tasks.Count(t => t.Status == TranslationTaskStatus.Skipped);
        int cancelled = tasks.Count(t => t.Status == TranslationTaskStatus.Cancelled);
        int pending = tasks.Count(t => t.Status is TranslationTaskStatus.Pending or TranslationTaskStatus.Paused);

        var summary = $"队列结束：成功 {completed} 张，失败 {failed} 张";
        if (partial > 0) summary += $"，部分完成 {partial} 张";
        if (skipped > 0) summary += $"，跳过 {skipped} 张";
        if (cancelled > 0) summary += $"，取消 {cancelled} 张";
        if (pending > 0) summary += $"，待处理 {pending} 张";

        var written = tasks.Count(t => !string.IsNullOrWhiteSpace(t.OutputPath));
        if (written > 0) summary += $"；已写回 {written} 份图纸到 {_config.ExportDirectory}";
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
    /// 启动时检测上次未完成的任务（任务层从 tasks.json 恢复）。
    /// 选择"继续"则直接开跑（沿用上次的写回方式，不再多弹一个对话框）；
    /// 选择"否"则清掉这些记录，界面回到干净状态。
    /// </summary>
    private void ResumePendingTasks()
    {
        var pending = _taskManager.PendingFromLastRun;
        if (pending.Count == 0) return;

        foreach (var task in pending)
        {
            if (DrawingFiles.All(r => !string.Equals(
                    NormalizeSourcePath(r.FullPath), NormalizeSourcePath(task.FilePath), StringComparison.OrdinalIgnoreCase)))
            {
                DrawingFiles.Add(new DrawingFileItem(task.FilePath) { Index = DrawingFiles.Count + 1 });
            }
        }

        AttachTasksToRows();
        HasDrawingFiles = DrawingFiles.Count > 0;
        HasMultipleDrawingFiles = DrawingFiles.Count > 1;

        var answer = Views.ConfirmDialog.Ask(
            Application.Current?.MainWindow,
            "未完成的任务",
            $"检测到上次有 {pending.Count} 张图纸没有跑完：\n\n    {string.Join("\n    ", pending.Take(8).Select(t => t.FileName))}"
                + (pending.Count > 8 ? $"\n    …等 {pending.Count} 张" : string.Empty)
                + "\n\n是否继续处理？（选择「取消」会清除这些未完成记录）",
            confirmText: "继续", danger: false) ? MessageBoxResult.Yes : MessageBoxResult.No;
        if (answer == MessageBoxResult.Yes)
        {
            // 不 await：OnLoaded 还在跑初始化，队列在后台自己跑完并回填界面。
            _ = RunTaskQueueAsync(retryFailedFirst: false, askWritebackMode: false);
        }
        else
        {
            _taskManager.Clear(includeUnfinished: true);
            StatusMessage = Strings.Get("StatusReady");
        }
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

