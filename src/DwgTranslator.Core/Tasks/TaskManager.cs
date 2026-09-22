using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Translation;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace DwgTranslator.Core.Tasks;

/// <summary>
/// 写回方式，对应导出时让用户选的"离线写回 / AutoCAD 写回"两种模式。
/// 任务层不弹对话框，因此由调用方（UI）在提交任务前把它设好。
/// </summary>
public enum TaskWritebackMode
{
    /// <summary>离线写回（ACadSharp），不要求装 CAD，批量跑最稳。</summary>
    Offline,

    /// <summary>AutoCAD COM 互操作写回，排版最接近原图，但要求 CAD 可用。</summary>
    AutoCad
}

/// <summary>
/// 任务层实现（<see cref="ITaskManager"/>）。UI 只提交图纸、订阅事件、看进度，
/// 按钮事件里不再直接读 DWG、不再直接请求 AI——这正是"任务层"存在的理由。
///
/// 双层限流：<see cref="TaskManagerOptions.LocalWorkerCount"/> 决定同时有几张图纸在跑
/// （本地解析/写回是 IO + CPU 混合，开太多只会抢磁盘），
/// <see cref="TaskManagerOptions.AiConcurrency"/> 决定单张图纸内部的 AI 请求并发
/// （接口 <see cref="ITranslationService"/> 没有并发参数，见下面两个构造函数里的说明）。
///
/// 故障隔离：每张图纸的整条流水线都在自己的 try/catch 里，单张失败只把这张图纸置 Failed，
/// 绝不打断队列里其它图纸（这也是这个类头号要保证的事情）。
///
/// 线程安全：<see cref="ITaskManager.Tasks"/> 加锁复制后再交出去，所有公开方法都可以被
/// UI 线程直接调用；事件在调用线程（通常是工作线程）上触发，订阅方自行 Dispatcher 调度。
/// </summary>
public sealed class TaskManager : ITaskManager, ITaskRecoveryDiagnostics, IRuntimeTaskConfiguration
{
    /// <summary>翻译任务只负责解析、提取、翻译和保存检查点；写回是独立导出操作。</summary>
    private const int StageCount = 4;

    [Obsolete("翻译阶段不再选择输出目录；请在独立导出流程中选择。")]
    public Func<CancellationToken, Task<string?>>? SelectOutputDirectoryAsync { get; set; }

    public void ApplyConfiguration(AppConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("Cannot change configuration during a queue run.");
            // 显式赋值替代反射拷贝：编译期即可发现属性改名/删除，且便于审计每个被任务管线消费的字段。
            _config.ProtectDimensions = config.ProtectDimensions;
            _config.ProtectTolerances = config.ProtectTolerances;
            _config.ProtectModels = config.ProtectModels;
            _config.GlossaryFirst = config.GlossaryFirst;
            _config.MaxTranslationConcurrency = config.MaxTranslationConcurrency;
            _config.LocalWorkerCount = config.LocalWorkerCount;
            _config.AiConcurrency = config.AiConcurrency;
            _config.MemoryOptimization = config.MemoryOptimization;
            _config.MaxRetryCount = config.MaxRetryCount;
            _config.ExportDirectory = config.ExportDirectory;
            _config.OutputNamingPattern = config.OutputNamingPattern;
            _config.DuplicatePolicy = config.DuplicatePolicy;
            _config.BackupSourceBeforeWrite = config.BackupSourceBeforeWrite;
            _config.AutoCadInstallPath = config.AutoCadInstallPath;
            _config.CadPluginPath = config.CadPluginPath;
            _config.OpenOutputFolderAfterExport = config.OpenOutputFolderAfterExport;
        }
    }

    private readonly IDwgReaderService _dwgReader;
    private readonly IDxfReaderService? _dxfReader;
    private readonly ITranslationService _translationService;
    private readonly IDwgWriterService _dwgWriter;
    private readonly IDxfWriterService? _dxfWriter;
    private readonly IAutoCadInteropService? _autoCadInterop;
    private ITaskStore _store;
    private bool _saveFailureReported;
    private readonly AppConfig _config;
    private readonly LayoutStatsProbe _layoutProbe;

    private readonly object _gate = new();
    private readonly List<TranslationTask> _tasks = new();
    private readonly List<TranslationTask> _pendingFromLastRun = new();

    /// <summary>
    /// 本队列的语言对。构造时取 AppConfig，之后可被 <see cref="ConfigureRun"/> 覆盖：
    /// 界面上的语言选择器是"每次开始翻译前都可以改"的，任务层再读一次启动时的配置就会翻错语言。
    /// </summary>
    private string _sourceLanguage;
    private string _targetLanguage;

    /// <summary>暂停前的状态，恢复时逐个还原（暂停不该把"翻译中"抹成"等待中"）。</summary>
    private readonly Dictionary<string, TranslationTaskStatus> _statusBeforePause = new(StringComparer.Ordinal);

    /// <summary>暂停闸门：暂停时非空，工作线程 await 它即停在阶段边界，不必轮询烧 CPU。</summary>
    private TaskCompletionSource<bool>? _pauseTcs;
    private bool _paused;
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _disposed;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    /// <summary>
    /// 注入式构造：翻译服务由调用方（DI 容器）提供。
    /// 注意：<see cref="ITranslationService"/> 的接口签名里没有并发参数，所以单张图纸内部的
    /// AI 并发由注入实例自己的构造参数（TranslationService 的 maxConcurrency）决定；
    /// 需要让 <see cref="TaskManagerOptions.AiConcurrency"/> 真正生效，请用下面那个收齐
    /// glossary/parser/client 的构造函数，任务层会按 AiConcurrency 去建 TranslationService。
    /// </summary>
    public TaskManager(
        IDwgReaderService dwgReader,
        IDxfReaderService? dxfReader,
        ITranslationService translationService,
        IDwgWriterService dwgWriter,
        IDxfWriterService? dxfWriter,
        ITaskStore store,
        TaskManagerOptions options,
        AppConfig config,
        IAutoCadInteropService? autoCadInterop = null)
        : this(dwgReader, dxfReader, translationService, dwgWriter, dxfWriter, store,
               options, config, autoCadInterop, new LayoutStatsProbe())
    {
    }

    /// <summary>
    /// 自建翻译服务的构造：复用 <see cref="TranslationService"/> 与
    /// MainViewModel.TranslateAsync 完全相同的构造参数，其中 maxConcurrency 取
    /// <see cref="TaskManagerOptions.AiConcurrency"/>——这就是"单张图纸内部 AI 并发"的落地点。
    /// batchSize / maxRetryCount 沿用 AppConfig，与现有单文件流程一致，保证功能不退化。
    /// </summary>
    public TaskManager(
        IDwgReaderService dwgReader,
        IDxfReaderService? dxfReader,
        IGlossaryService glossaryService,
        IFormatCodeParser formatCodeParser,
        IDeepSeekClient deepSeekClient,
        IDwgWriterService dwgWriter,
        IDxfWriterService? dxfWriter,
        ITaskStore store,
        TaskManagerOptions options,
        AppConfig config,
        string systemPrompt,
        IAutoCadInteropService? autoCadInterop = null,
        ITranslationConsistencyService? consistencyService = null)
        : this(
            dwgReader, dxfReader,
            new TranslationService(
                glossaryService, formatCodeParser, deepSeekClient, systemPrompt,
                config.BatchSize, config.MaxRetryCount, consistencyService,
                // TranslationService 内部会再夹到 1..20；这里先夹一次是为了把 0 挡在构造之前，
                // 否则 SemaphoreSlim(0) 会让所有 AI 请求永久排队。
                maxConcurrency: Math.Min(20, Math.Max(1, options.AiConcurrency))),
            dwgWriter, dxfWriter, store, options, config, autoCadInterop, new LayoutStatsProbe())
    {
    }

    private TaskManager(
        IDwgReaderService dwgReader,
        IDxfReaderService? dxfReader,
        ITranslationService translationService,
        IDwgWriterService dwgWriter,
        IDxfWriterService? dxfWriter,
        ITaskStore store,
        TaskManagerOptions options,
        AppConfig config,
        IAutoCadInteropService? autoCadInterop,
        LayoutStatsProbe layoutProbe)
    {
        _dwgReader = dwgReader ?? throw new ArgumentNullException(nameof(dwgReader));
        _dxfReader = dxfReader;
        _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
        _dwgWriter = dwgWriter ?? throw new ArgumentNullException(nameof(dwgWriter));
        _dxfWriter = dxfWriter;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _autoCadInterop = autoCadInterop;
        _layoutProbe = layoutProbe;

        // 不复制 Options：调用方（设置页）可能在线改并发数，复制会变成"改了不生效"。
        // 越界值在每次使用时夹住，详见 LocalWorkerCount / AiConcurrency 两个属性。
        Options = options ?? throw new ArgumentNullException(nameof(options));

        _sourceLanguage = TranslationLanguages.Normalize(_config.SourceLanguage);
        _targetLanguage = TranslationLanguages.Normalize(_config.TargetLanguage);

        RestoreFromStore();
    }

    /// <inheritdoc/>
    public TaskManagerOptions Options { get; }

    /// <summary>同时处理的图纸数（1..6）。手改 settings.json 写成 0 会让队列永远不动，这里夹住。</summary>
    private int LocalWorkerCount => Math.Min(6, Math.Max(1, Options.LocalWorkerCount));

    /// <summary>单张图纸内部的 AI 并发（1..8），自建 TranslationService 时作为 maxConcurrency 传入。</summary>
    private int AiConcurrency => Math.Min(8, Math.Max(1, Options.AiConcurrency));

    /// <summary>任务列表副本。每次访问都复制一份，UI 遍历时不会被工作线程改动。</summary>
    public IReadOnlyList<TranslationTask> Tasks
    {
        get { lock (_gate) return _tasks.ToList(); }
    }

    /// <summary>
    /// 上次运行遗留、可供继续的任务（构造时从 <see cref="ITaskStore"/> 恢复）。
    /// UI 用它弹"检测到 N 张未完成，是否继续"：这些任务已经在 <see cref="Tasks"/> 里显示为
    /// 等待中，但不会自己开跑——只有调用 <see cref="RunAsync"/> 才执行；用户选"不继续"时
    /// 调 <see cref="Clear"/> 清掉即可。
    /// </summary>
    public IReadOnlyList<TranslationTask> PendingFromLastRun
    {
        get { lock (_gate) return _pendingFromLastRun.ToList(); }
    }

    /// <summary>队列是否处于暂停状态（按钮文案用）。</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>是否有队列正在执行（RunAsync 期间的重复调用会被忽略）。</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>本队列的语言对（已规范化）。UI 只读用于日志与显示，修改请走 <see cref="ConfigureRun"/>。</summary>
    public string SourceLanguage => _sourceLanguage;

    public string TargetLanguage => _targetLanguage;

    [Obsolete("输出目录已迁移到独立导出服务。")]
    public string? OutputDirectory { get; set; }

    [Obsolete("写回模式已迁移到独立导出服务。")]
    public TaskWritebackMode WritebackMode { get; set; } = TaskWritebackMode.Offline;

    /// <inheritdoc/>
    public void ConfigureRun(string sourceLanguage, string targetLanguage)
    {
        ThrowIfDisposed();
        _sourceLanguage = TranslationLanguages.Normalize(sourceLanguage);
        _targetLanguage = TranslationLanguages.Normalize(targetLanguage);
        Log.Information("翻译任务运行参数：{Source} → {Target}；写回与输出目录将在项目校对后单独选择",
            _sourceLanguage, _targetLanguage);
    }

    [Obsolete("翻译任务只接受语言参数；输出设置会被忽略。")]
    public void ConfigureRun(string sourceLanguage, string targetLanguage, string? outputDirectory, TaskWritebackMode? writebackMode)
    {
        ConfigureRun(sourceLanguage, targetLanguage);
    }

    public void AssignProject(string taskId, string projectId)    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("任务 ID 不能为空", nameof(taskId));
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("项目 ID 不能为空", nameof(projectId));
        lock (_gate)
        {
            var task = _tasks.SingleOrDefault(item => item.Id == taskId)
                ?? throw new ArgumentException("找不到该图纸任务", nameof(taskId));
            task.ProjectId = projectId.Trim();
            TouchTask(task);
        }
        SaveNow();
    }

    /// <inheritdoc/>
    public void MarkReviewCompleted(string taskId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("任务 ID 不能为空", nameof(taskId));

        TranslationTask task;
        lock (_gate)
        {
            task = _tasks.SingleOrDefault(item => item.Id == taskId)
                ?? throw new ArgumentException("找不到该图纸任务", nameof(taskId));
            if (task.Status != TranslationTaskStatus.ReadyForReview)
                throw new InvalidOperationException($"任务“{task.FileName}”当前为{task.StatusText}，不能标记为校对完成。");

            var now = DateTime.UtcNow;
            task.TransitionTo(TranslationTaskStatus.Completed, TranslationTaskTransitionReason.ReviewCompleted, now);
            task.Progress = 100;
            task.CompletedAt ??= now;
            task.ReviewCompletedAt = now;
        }

        SaveNow();
        RaiseTaskUpdated(task);
        RaiseOverallProgress();
    }

    /// <inheritdoc/>
    public void RecordExportPath(string taskId, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("任务 ID 不能为空", nameof(taskId));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("输出路径不能为空", nameof(outputPath));

        var normalizedPath = Path.GetFullPath(outputPath.Trim());
        TranslationTask task;
        lock (_gate)
        {
            task = _tasks.SingleOrDefault(item => item.Id == taskId)
                ?? throw new ArgumentException("找不到该图纸任务", nameof(taskId));
            task.LastExportPath = normalizedPath;
            var now = DateTime.UtcNow;
            task.LastExportedAt = now;
            task.UpdatedAt = now;
            // A task loaded from pre-project versions may still carry the obsolete field.
            // Once a project export is recorded, clear it so the UI cannot display a stale
            // filename after a retry or a new export.
            task.OutputPath = null;
        }

        SaveNow();
        RaiseTaskUpdated(task);
    }
    public event EventHandler<TranslationTask>? TaskUpdated;

    /// <inheritdoc/>
    public event EventHandler<string>? ProgressMessage;

    /// <inheritdoc/>
    public event EventHandler<double>? OverallProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<TranslationPair>? TranslationCompleted;

    // ───────────────────────────── 队列操作 ─────────────────────────────

    /// <inheritdoc/>
    public TranslationTask Enqueue(string filePath, TaskPriority priority = TaskPriority.Normal)
    {
        var task = EnqueueInternal(filePath, priority);
        SaveNow();
        return task;
    }

    /// <summary>入队核心逻辑；持久化由调用方决定（单张立即保存，批量入队最后统一保存一次）。</summary>
    private TranslationTask EnqueueInternal(string filePath, TaskPriority priority)
    {
        ThrowIfDisposed();

        var fullPath = NormalizePath(filePath);
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("图纸路径不能为空", nameof(filePath));

        TranslationTask task;
        lock (_gate)
        {
            // 同一张图纸重复入队：直接返回已有任务。否则两张图纸会同时写同一个输出文件，
            // 后写的把先写的顶掉，用户看到的是"随机少了一张"。
            var existing = _tasks.FirstOrDefault(t =>
                !t.IsFinished && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Log.Warning("图纸已在队列中，忽略重复入队：{File}", existing.FileName);
                return existing;
            }

            task = new TranslationTask(fullPath, priority);
            _tasks.Add(task);
        }

        RaiseTaskUpdated(task);
        RaiseOverallProgress();
        Log.Information("任务入队：{File}（优先级 {Priority}，队列 {Count}）", task.FileName, task.PriorityText, Tasks.Count);
        return task;
    }

    /// <inheritdoc/>
    public void EnqueueRange(IEnumerable<string> filePaths, TaskPriority priority = TaskPriority.Normal)
    {
        ThrowIfDisposed();
        if (filePaths == null) return;

        var enqueued = false;
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                EnqueueInternal(path, priority);
                enqueued = true;
            }
            catch (Exception ex)
            {
                // 批量拖入时一个坏路径不该让整批都进不去队列。
                Log.Warning(ex, "图纸入队失败，已跳过：{Path}", path);
            }
        }

        // 批量入队只落盘一次：逐张 SaveNow 会把 N 张图纸放大成 N 次全量序列化。
        if (enqueued) SaveNow();
    }

    /// <inheritdoc/>
    public void Remove(TranslationTask task)
    {
        ThrowIfDisposed();
        if (task == null) return;

        bool removed;
        lock (_gate)
        {
            if (task.IsActive)
            {
                // 工作线程还持有这个对象，抽走它只会让状态更新落到队列外。
                Log.Warning("任务正在执行，无法从队列移除：{File}", task.FileName);
                return;
            }

            removed = _tasks.Remove(task) || _tasks.RemoveAll(t => t.Id == task.Id) > 0;
            if (removed) _pendingFromLastRun.RemoveAll(t => t.Id == task.Id);
        }

        if (!removed) return;
        RaiseOverallProgress();
        SaveNow();
    }

    /// <inheritdoc/>
    public void Clear(bool includeUnfinished = false)
    {
        ThrowIfDisposed();

        int keptActive = 0;
        lock (_gate)
        {
            // 运行中的任务不能抽走：它就是当前这张正在写回的图纸。
            var doomed = _tasks
                .Where(t => includeUnfinished ? !t.IsActive : t.IsFinished)
                .ToList();

            if (includeUnfinished)
                keptActive = _tasks.Count(t => t.IsActive);

            foreach (var task in doomed) _tasks.Remove(task);
            _pendingFromLastRun.RemoveAll(t => !_tasks.Contains(t));
        }

        if (keptActive > 0)
            Log.Warning("{Count} 个任务正在执行，未参与清理", keptActive);

        RaiseOverallProgress();
        SaveNow();
        Log.Information("任务列表已清理（{Mode}）", includeUnfinished ? "含未完成" : "仅已结束");
    }

    // ───────────────────────────── 执行 ─────────────────────────────

    /// <inheritdoc/>
    public Task RunAsync(CancellationToken cancellationToken = default) => RunSelectedAsync(null, cancellationToken);

    public Task RetryTaskAsync(string taskId, CancellationToken cancellationToken = default) => RunSelectedAsync(taskId, cancellationToken);

    private async Task RunSelectedAsync(string? taskId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running)
            {
                Log.Warning("队列已在运行，忽略本次 RunAsync");
                return;
            }

            if (taskId != null)
            {
                var task = _tasks.SingleOrDefault(t => t.Id == taskId)
                    ?? throw new ArgumentException("找不到该图纸任务", nameof(taskId));
                ResetForRetry(task);
                task.RetryCount++;
                TouchTask(task);
            }
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _runCts;
            _running = true;
        }

        try
        {
            var queue = TakeRunnableQueue().Where(t => taskId == null || t.Id == taskId).ToList();
            if (queue.Count == 0)
            {
                Log.Information("队列里没有待处理的图纸");
                return;
            }

            RaiseProgressMessage($"[0/{StageCount}] 队列开始：{queue.Count} 张图纸，本地并发 {LocalWorkerCount}，单图纸 AI 并发 {AiConcurrency}");

            // 整段调度都在线程池线程上：RunAsync 返回的 Task 可以给 UI await，
            // 但 UI 线程永远不执行解析 / 翻译 / 写回本身。
            await Task.Run(async () =>
            {
                using var workerGate = new SemaphoreSlim(LocalWorkerCount, LocalWorkerCount);
                var running = new List<Task>();

                try
                {
                    foreach (var task in queue)
                    {
                        // 第一层限流：等有空闲的"图纸槽位"才开工。
                        await workerGate.WaitAsync(cts.Token).ConfigureAwait(false);
                        running.Add(ProcessTaskAsync(task, workerGate, cts.Token));
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Information("队列调度被取消：尚未开始的图纸保持等待状态");
                }
                finally
                {
                    // 让已经开工的图纸收尾（ProcessTaskAsync 内部消化所有异常，
                    // 这里兜底只是不让任何异常逃出 RunAsync 打死 UI 的 await）。
                    try { await Task.WhenAll(running).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Error(ex, "队列收尾时出现未预期异常"); }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _runCts = null;
            }

            cts.Dispose();
            SaveNow();
            RaiseOverallProgress();
            RaiseProgressMessage(
                $"[{StageCount}/{StageCount}] 队列结束：成功 {CountBy(TranslationTaskStatus.Completed)}，失败 {CountBy(TranslationTaskStatus.Failed)}，取消 {CountBy(TranslationTaskStatus.Cancelled)}");
        }
    }

    /// <inheritdoc/>
    public void PauseAll()
    {
        ThrowIfDisposed();

        List<TranslationTask> paused = new();
        TaskCompletionSource<bool> signal;

        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseTcs = signal;

            _statusBeforePause.Clear();
            foreach (var task in _tasks)
            {
                if (task.IsFinished || task.Status == TranslationTaskStatus.Paused) continue;
                _statusBeforePause[task.Id] = task.Status;
                task.TransitionTo(TranslationTaskStatus.Paused, TranslationTaskTransitionReason.PausedByUser);
                paused.Add(task);
            }
        }

        foreach (var task in paused) RaiseTaskUpdated(task);
        RaiseProgressMessage($"[暂停] {paused.Count} 个任务停在当前阶段，队列不再推进");
        SaveNow();
        Log.Information("队列已暂停，{Count} 个任务进入 Paused", paused.Count);
    }

    /// <inheritdoc/>
    public void ResumeAll()
    {
        ThrowIfDisposed();

        List<TranslationTask> resumed = new();
        TaskCompletionSource<bool>? signal;

        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            signal = _pauseTcs;
            _pauseTcs = null;

            foreach (var task in _tasks)
            {
                if (task.Status != TranslationTaskStatus.Paused) continue;
                var previous = _statusBeforePause.TryGetValue(task.Id, out var status)
                    ? status
                    : TranslationTaskStatus.Pending;
                task.TransitionTo(previous == TranslationTaskStatus.Paused ? TranslationTaskStatus.Pending : previous,
                    TranslationTaskTransitionReason.ResumedByUser);
                resumed.Add(task);
            }

            _statusBeforePause.Clear();
        }

        // 放行所有停在阶段边界的工作线程；跑完当前阶段的图纸从当前阶段接着走，不重头来。
        signal?.TrySetResult(true);

        foreach (var task in resumed) RaiseTaskUpdated(task);
        RaiseProgressMessage($"[继续] {resumed.Count} 个任务从当前阶段继续");
        SaveNow();
    }

    /// <inheritdoc/>
    public void CancelAll()
    {
        ThrowIfDisposed();

        List<TranslationTask> cancelled = new();
        TaskCompletionSource<bool>? signal;
        CancellationTokenSource? runCts;

        lock (_gate)
        {
            _paused = false;
            signal = _pauseTcs;
            _pauseTcs = null;
            _statusBeforePause.Clear();
            runCts = _runCts;

            foreach (var task in _tasks)
            {
                // 正在执行的图纸交给取消令牌（它会走到取消分支并置 Cancelled）；
                // 已经结束的记录保持原样，取消不该抹掉历史。
                if (task.Status is not (TranslationTaskStatus.Pending or TranslationTaskStatus.Paused)) continue;

                var cancelledAt = DateTime.UtcNow;
                task.TransitionTo(TranslationTaskStatus.Cancelled, TranslationTaskTransitionReason.CancelledByUser, cancelledAt);
                task.CompletedAt = cancelledAt;
                task.Error = null;
                cancelled.Add(task);
            }
        }

        signal?.TrySetResult(true);
        runCts?.Cancel();

        foreach (var task in cancelled) RaiseTaskUpdated(task);
        RaiseOverallProgress();
        SaveNow();
        Log.Information("已取消 {Count} 个等待中的任务", cancelled.Count);
    }

    /// <inheritdoc/>
    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        List<TranslationTask> retried;
        lock (_gate)
        {
            retried = _tasks.Where(t => t.Status is TranslationTaskStatus.Failed or TranslationTaskStatus.PartiallyCompleted).ToList();
            foreach (var task in retried)
            {
                ResetForRetry(task);
                task.RetryCount++;
                TouchTask(task);
            }
        }

        if (retried.Count == 0)
        {
            Log.Information("没有失败的任务可供重试");
            return;
        }

        foreach (var task in retried) RaiseTaskUpdated(task);
        RaiseOverallProgress();
        SaveNow();
        Log.Information("重试 {Count} 个失败任务", retried.Count);

        await RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void CancelCurrentRun()
    {
        ThrowIfDisposed();

        CancellationTokenSource? runCts;
        lock (_gate) runCts = _runCts;

        if (runCts == null)
        {
            Log.Information("当前没有正在执行的队列");
            return;
        }

        runCts.Cancel();
        Log.Information("已请求取消当前队列（已完成的记录保留）");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            lock (_gate)
            {
                _pauseTcs?.TrySetResult(true);
                _pauseTcs = null;
                _paused = false;
            }

            _runCts?.Cancel();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "TaskManager 释放时取消队列失败");
        }

        // 探针还链在 Serilog 上，退出时还原，别给宿主留一个统计用的 logger。
        _layoutProbe.Dispose();
        SaveNow();
        Log.Debug("TaskManager 已释放");
    }

    internal string BuildCheckpointSignature(string sourceFilePath, CancellationToken cancellationToken = default)
    {
        var fileHash = HashFile(sourceFilePath, cancellationToken);

        var serviceContext = _translationService is ITranslationCheckpointContextProvider provider
            ? provider.GetCheckpointContext(_sourceLanguage, _targetLanguage)
            : "service-type-v1|" + (_translationService.GetType().FullName ?? _translationService.GetType().Name);
        var semanticContext = System.Text.Json.JsonSerializer.Serialize(new
        {
            Schema = 2,
            SourceLanguage = TranslationLanguages.Normalize(_sourceLanguage),
            TargetLanguage = TranslationLanguages.Normalize(_targetLanguage),
            _config.ProtectDimensions,
            _config.ProtectTolerances,
            _config.ProtectModels,
            _config.GlossaryFirst,
            ServiceContext = serviceContext
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fileHash + "|" + semanticContext)));
    }

    /// <summary>
    /// 分块计算图纸哈希。以前是一次性把整份文件读进 SHA256.HashData：大图纸要等它跑完，
    /// 用户点了取消也中断不了；现在按块喂哈希并逐块检查取消令牌，同时把"文件被占用"
    /// 单独翻译成用户看得懂的说明（原来的 IOException 文案只说"被另一个进程使用"，
    /// 指向不了"哪张图、为什么"，现场只能靠猜）。
    /// </summary>
    private static string HashFile(string sourceFilePath, CancellationToken cancellationToken)
    {
        const int BufferSize = 1024 * 1024;
        try
        {
            using var stream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex)
        {
            throw new IOException(
                $"图纸文件无法读取，无法建立翻译断点（可能被其它程序独占或网络盘断开）：{sourceFilePath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"没有读取图纸文件的权限，无法建立翻译断点：{sourceFilePath}", ex);
        }
    }

    // ───────────────────────────── 单张图纸流水线 ─────────────────────────────

    /// <summary>
    /// 跑完一张图纸的 4 个翻译阶段。任何一步抛异常都只影响这一张图纸：
    /// 置 Failed + 写 Error，然后正常归还槽位，队列继续下一张。
    /// </summary>
    private async Task ProcessTaskAsync(TranslationTask task, SemaphoreSlim workerGate, CancellationToken ct)
    {
        try
        {
            // 暂停时停在阶段边界：这张图纸还没占用 AI 配额，恢复后从当前阶段继续。
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            MarkStarted(task);
            // ── [1/4] Parsing：读 DWG/DXF ──
            SetStatus(task, TranslationTaskStatus.Parsing);
            RaiseProgressMessage(Stage(task, 1, "正在解析图纸"));
            var entities = await Task.Run(() => ReadTextEntities(task), ct).ConfigureAwait(false);
            if (_translationService is IAsyncTranslationCheckpointContextProvider asyncContextProvider)
                await asyncContextProvider.PrepareCheckpointContextAsync(_sourceLanguage, _targetLanguage, ct).ConfigureAwait(false);
            var signature = BuildCheckpointSignature(task.FilePath, ct);
            lock (_gate)
            {
                if (task.CheckpointSignature != signature) task.SuccessfulTranslations = new();
                task.CheckpointSignature = signature;
                // 断点续译可能带回上万条已完成条目：逐条线性查找会让这一步退化成 O(n²)，
                // 先按 (Handle, 原文) 建索引再匹配实体。
                var savedByKey = task.SuccessfulTranslations
                    .GroupBy(p => (p.Handle, p.SourceText))
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (var entity in entities)
                {
                    if (!savedByKey.TryGetValue((entity.Handle, entity.PlainText), out var saved)) continue;
                    entity.TranslatedText = saved.TranslatedText;
                    entity.Status = saved.Status;
                    task.TranslatedCount++;
                }
            }

            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            // ── [2/6] Extracting：挑出可译文本，记下条数（进度分母） ──
            var toTranslate = entities
                .Where(e => !e.IsXref
                    && e.Status == TranslationStatus.Pending
                    && !AttributeTranslationPolicy.IsMetadataHandle(e.Handle))
                .ToList();

            lock (_gate)
            {
                task.TextCount = entities.Count;
                task.Progress = 0;
            }

            SetStatus(task, TranslationTaskStatus.Extracting);
            RaiseProgressMessage(entities.Count == 0
                ? Stage(task, 2, "图纸里没有文本，未生成输出文件")
                : Stage(task, 2, $"提取到 {entities.Count:N0} 个文本，其中 {toTranslate.Count:N0} 条待翻译"));
            SaveNow();

            if (entities.Count == 0)
            {
                // 空图纸不是错误：没有可译文本，也就没有必要写出一份副本。
                Finish(task, "图纸里没有文本");
                return;
            }

            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            // ── [3/6] Translating：复用 TranslationService 的去重并发管线 ──
            SetStatus(task, TranslationTaskStatus.Translating);
            RaiseProgressMessage(Stage(task, 3, "正在翻译文本", 0));

            if (toTranslate.Count > 0)
            {
                var progress = BuildTranslationProgress(task, toTranslate.Count);
                // Translation no longer asks for an export/writeback mode. Worker billing still requires
                // its own stable online/offline value, so new and legacy "translation" tasks use online.
                var billingMode = string.Equals(task.BillingMode, "offline", StringComparison.OrdinalIgnoreCase)
                    ? "offline"
                    : "online";
                task.BillingMode = billingMode;
                using var billingScope = TranslationBillingContext.Enter(task.Id, billingMode);
                SaveNow();
                var pairs = await _translationService
                    .TranslateBatchWithProgressAsync(
                        toTranslate, _sourceLanguage, _targetLanguage, progress, ct)
                    .ConfigureAwait(false);

                ApplyTranslationResults(toTranslate, pairs);
                // Flush the completed batch before CAD dispatch; throttled progress saves
                // can otherwise leave a fast batch absent from crash-recovery state.
                SaveNow();

                Log.Information("图纸 {File}：翻译阶段结束，返回 {Pairs} 条译文", task.FileName, pairs?.Count ?? 0);
            }

            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            // ── [4/4] ReadyForReview：保存译文检查点，等待用户校对后显式导出 ──
            var reviewSet = BuildWritebackSet(task, entities);
            if (reviewSet.Count == 0)
            {
                if (task.FailedCount > 0)
                    throw new InvalidOperationException("没有可校对的译文，所有待翻译条目均失败");
                Finish(task, "图纸中没有需要校对的译文");
                return;
            }

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                task.TransitionTo(TranslationTaskStatus.ReadyForReview, TranslationTaskTransitionReason.AwaitingReview, now);
                task.Progress = 100;
                task.CompletedAt = now;
                task.ReviewCompletedAt = null;
                task.LastExportedAt = null;
                task.LastExportPath = null;
                task.OutputPath = null;
            }
            // 校对是可选的人工复核（2026-09-22 产品调整），这条进度消息不再把它写成下一步。
            RaiseProgressMessage(Stage(task, 4, $"翻译完成：{task.TranslatedCount:N0} 条，可以导出（校对可选）", 100));
            SaveNow();
            RaiseTaskUpdated(task);
            RaiseOverallProgress();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            MarkCancelled(task);
        }
        catch (Exception ex)
        {
            MarkFailed(task, ex);
        }
        finally
        {
            // 槽位一定要归还，否则一次异常就让队列少一格并发。
            workerGate.Release();
        }
    }

    /// <summary>
    /// 翻译进度回调必须同步执行，保证计数与完成事件先于翻译 Task 返回。这里已经在线程池线程上
    /// （RunAsync 内层 Task.Run）。Progress 即使没有 SynchronizationContext 也会异步 Post，
    /// 因此使用 InlineProgress；UI 订阅方仍自行 Dispatcher 调度，不阻塞 UI。
    /// </summary>
    /// <summary>Task state changes are part of translation completion, not fire-and-forget work.</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
    private IProgress<TranslationPair> BuildTranslationProgress(TranslationTask task, int total)
    {
        int completed = 0;
        long lastMessageTicks = 0;
        long lastRaiseTicks = 0;

        return new InlineProgress<TranslationPair>(pair =>
        {
            try
            {
                int done = Interlocked.Increment(ref completed);
                double percent;

                lock (_gate)
                {
                    // TranslatedCount 只算真正拿到译文的（含术语命中/跳过），失败单独计，
                    // 否则"已翻译 N 条"会把失败条数也算进去。
                    if (pair.Status == TranslationStatus.TranslationFailed) task.FailedCount++;
                    else
                    {
                        task.TranslatedCount++;
                        task.SuccessfulTranslations = task.SuccessfulTranslations.Where(p => p.Handle != pair.Handle).Append(pair).ToList();
                    }

                    percent = total <= 0 ? 100 : Math.Min(100, (double)done / total * 100.0);
                    task.Progress = percent;
                    TouchTask(task);
                }

                // TaskUpdated 按 100ms/任务节流，最后一条（done >= total）强制发出保证终态可见；
                // 数据本身（计数/进度值）始终在锁内即时更新，节流只影响事件触发频率。
                var raiseNow = DateTime.UtcNow.Ticks;
                var lastRaise = Interlocked.Read(ref lastRaiseTicks);
                if (done >= total || raiseNow - lastRaise >= TimeSpan.TicksPerMillisecond * 100)
                {
                    Interlocked.Exchange(ref lastRaiseTicks, raiseNow);
                    RaiseTaskUpdated(task);
                }

                var now = DateTime.UtcNow.Ticks;
                var previous = Interlocked.Read(ref lastMessageTicks);
                if (done >= total || now - previous >= TimeSpan.TicksPerMillisecond * 400)
                {
                    Interlocked.Exchange(ref lastMessageTicks, now);
                    RaiseProgressMessage(Stage(task, 3, "正在翻译文本", percent));
                }

                RaiseOverallProgress();
                SaveThrottled();

                // 实体级回调：UI 的「文字条目表」（人工校对 / Excel 复核）必须与任务层看到同一份译文，
                // 否则界面会显示"翻译完成"但条目表全是待翻译。订阅方自己吞异常，这里也再兜一层。
                try { TranslationCompleted?.Invoke(this, pair); }
                catch (Exception ex) { Log.Debug(ex, "翻译完成事件订阅方异常已忽略：{File}", task.FileName); }
            }
            catch (Exception ex)
            {
                // 回调是在 TranslationService 的 foreach 里被同步调用的，
                // 这里抛出去会把整批翻译判死——统计用途的异常必须自己吞掉。
                Log.Debug(ex, "翻译进度回调异常已忽略：{File}", task.FileName);
            }
        });
    }

    /// <summary>
    /// 读文本实体：与 MainViewModel.ImportCadFilesAsync 完全同一条调用序列
    /// （按扩展名选 DXF/DWG reader，再补 SourceFilePath / Notes），保证任务层与单文件流程行为一致。
    /// </summary>
    private List<TextEntity> ReadTextEntities(TranslationTask task)
    {
        var path = task.FilePath;
        var extension = Path.GetExtension(path).ToLowerInvariant();

        List<TextEntity> entities = extension == ".dxf"
            ? _dxfReader?.ExtractFromFile(path) ?? new List<TextEntity>()
            : _dwgReader.ExtractFromFile(path);

        // 句柄只在单张图纸内唯一，所以导入时把来源路径写进每个实体（写回也按它圈定范围）。
        var fullPath = Path.GetFullPath(path);
        foreach (var entity in entities)
        {
            entity.SourceFilePath = fullPath;
            entity.Notes = $"Source: {Path.GetFileNameWithoutExtension(path)}";
        }

        return entities;
    }

    /// <summary>Validate returned pair identities and apply only translation result fields.</summary>
    private static void ApplyTranslationResults(List<TextEntity> entities, List<TranslationPair> pairs)
    {
        // Services may return pairs without mutating input entities. Match only this drawing's
        // original items; never reuse a handle from another source or restore formatting twice.
        var originals = entities.ToLookup(e =>
            (NormalizePath(e.SourceFilePath).ToUpperInvariant(), e.Handle, e.PlainText));
        var assignments = new List<(TextEntity Entity, TranslationPair Pair)>();
        var seen = new HashSet<TextEntity>();
        foreach (var pair in pairs)
        {
            var matches = originals[(NormalizePath(pair.SourceFilePath).ToUpperInvariant(),
                pair.Handle, pair.SourceText)].ToList();
            if (matches.Count != 1 || !seen.Add(matches[0]))
                throw new InvalidOperationException("译文结果与当前图纸条目不匹配或重复，已停止写回");
            assignments.Add((matches[0], pair));
        }

        // Validate the entire result set before applying any returned values.
        foreach (var (entity, pair) in assignments)
        {
            entity.TranslatedText = pair.TranslatedText;
            entity.Status = pair.Status;
            entity.GlossaryHit = pair.GlossaryHit;
        }
    }
    /// <summary>
    /// 圈定要写回的实体：先按 MainViewModel.ExportDwgAsync 的状态筛选，
    /// 再过一次导出前的完整性闸门（仍含原文的译文一律不写进图纸）。
    /// </summary>
    private List<TextEntity> BuildWritebackSet(TranslationTask task, List<TextEntity> entities)
    {
        var candidates = entities
            .Where(e => e.Status is TranslationStatus.Translated or TranslationStatus.Reviewed
                or TranslationStatus.WritebackSuccess or TranslationStatus.WritebackFailed)
            .ToList();

        var writebackGlossary = (_translationService as IWritebackGlossaryProvider)?.GetWritebackGlossary(_sourceLanguage, _targetLanguage);
        var invalid = candidates
            .Where(e => !TranslationQualityValidator.IsAcceptableCadText(
                e.PlainText, e.TranslatedText, _sourceLanguage, _targetLanguage, writebackGlossary))
            .ToList();

        if (invalid.Count > 0)
        {
            foreach (var entity in invalid)
                entity.Status = TranslationStatus.TranslationFailed;

            lock (_gate) task.FailedCount += invalid.Count;

            Log.Warning("图纸 {File}：{Count} 条译文仍包含原文，已阻止写回", task.FileName, invalid.Count);
            candidates = candidates.Where(e => e.Status != TranslationStatus.TranslationFailed).ToList();
        }

        return candidates;
    }

    /// <summary>
    /// 写回。与 MainViewModel.ExecuteWritebackWithMode 调用同一组服务方法，
    /// 区别只是这里不弹模式对话框（模式由 <see cref="WritebackMode"/> 决定）。
    /// </summary>
    /// <summary>
    /// 路径规范化。非法字符等极端情况退回原字符串：路径规范化失败只是一次入队不完美，
    /// 不该把这张图纸直接判死（真正的错误会在读文件时以更清楚的信息暴露出来）。
    /// </summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    /// <summary>节流保存：进度回调用 1 秒窗口，避免每翻译一条就把整表序列化一次。</summary>
    private void SaveThrottled()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastSaveUtc < TimeSpan.FromSeconds(1)) return;
            _lastSaveUtc = DateTime.UtcNow;
        }

        SaveNow();
    }

    /// <summary>阶段结束 / 入队出队 / 任务结束等关键点立即写盘；失败提示但不停止翻译。</summary>
    private void SaveNow()
    {
        // 快照在锁内完成，文件 IO 放到锁外：序列化+写盘期间不再阻塞入队/进度回调等所有 _gate 消费者。
        List<TranslationTask> snapshot;
        lock (_gate) snapshot = _tasks.ToList();
        try
        {
            _store.Save(snapshot);
            ReportSaveStatus(_store is ITaskStoreDiagnostics diagnostics && diagnostics.LastSaveFailed);
            lock (_gate) _lastSaveUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "任务状态保存失败（继续运行）");
            ReportSaveStatus(true);
        }
    }

    /// <summary>Report transitions only, avoiding repeated warnings on every progress save.</summary>
    private void ReportSaveStatus(bool failed)
    {
        if (failed == _saveFailureReported) return;
        _saveFailureReported = failed;
        RaiseProgressMessage(failed
            ? "[保存警告] 任务进度未能保存，重启后可能无法恢复本次进度。请检查磁盘空间和目录权限；当前翻译继续。"
            : "[保存恢复] 任务进度已重新成功保存。");
    }

    /// <summary>Copy queue membership under lock; task instances remain shared.</summary>
    private List<TranslationTask> Snapshot()
    {
        lock (_gate) return _tasks.ToList();
    }

    // ───────────────────────────── 状态与事件 ─────────────────────────────

    /// <summary>
    /// 恢复上次运行遗留的任务。恢复出来的任务只是"显示在列表里、标记为等待"，
    /// 不会自动开跑——用户通过 <see cref="PendingFromLastRun"/> 确认后才调用 RunAsync。
    /// </summary>
    /// <summary>Switch only while idle; flush old state before loading a different owner's store.</summary>
    /// <inheritdoc/>
    public void SwitchAccountStore(ITaskStore store, Action? commitSession = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("任务执行期间不能切换账号。");
            EnsureAccountStoreSaved();
            // Session persistence must succeed before discarding the current in-memory owner.
            commitSession?.Invoke();
            _store = store;
            _saveFailureReported = false;
            _tasks.Clear();
            _pendingFromLastRun.Clear();
            RestoreFromStore();
        }
    }

    /// <summary>Fail closed before account authentication or revocation when the queue cannot be saved.</summary>
    /// <inheritdoc/>
    public void EnsureAccountStoreSaved()
    {
        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("任务执行期间不能切换账号。");
            try
            {
                _store.Save(_tasks.ToList());
                var failed = _store is ITaskStoreDiagnostics diagnostics && diagnostics.LastSaveFailed;
                ReportSaveStatus(failed);
                if (failed) throw new IOException("当前任务未能保存，已阻止账号切换。请检查磁盘空间和目录权限后重试。");
            }
            catch
            {
                ReportSaveStatus(true);
                throw;
            }
        }
    }

    public string? RecoveryWarning
    {
        get { lock (_gate) return (_store as ITaskRecoveryDiagnostics)?.RecoveryWarning; }
    }

    private static void TouchTask(TranslationTask task, DateTime? timestamp = null)
    {
        var value = timestamp ?? DateTime.UtcNow;
        if (task.CreatedAt != default && value < task.CreatedAt) value = task.CreatedAt;
        if (task.UpdatedAt != default && value < task.UpdatedAt) value = task.UpdatedAt;
        task.UpdatedAt = value;
    }

    private static void ResetForRetry(TranslationTask task)
    {
        task.TransitionTo(TranslationTaskStatus.Pending, TranslationTaskTransitionReason.RetryRequested);
        task.Progress = 0;
        task.TextCount = 0;
        task.TranslatedCount = 0;
        task.FailedCount = 0;
        task.AutoScaledLabelCount = 0;
        task.InterferenceResolvedCount = 0;
        task.Error = null;
        task.StartedAt = null;
        task.CompletedAt = null;
        task.ReviewCompletedAt = null;
        task.LastExportedAt = null;
        task.LastExportPath = null;
        task.OutputPath = null;
    }

    private static void NormalizeRestoredTask(TranslationTask task)
    {
        task.SuccessfulTranslations ??= new List<TranslationPair>();
        task.CheckpointSignature ??= string.Empty;
        task.NormalizeAuditMetadata();
    }

    private void RestoreFromStore()
    {
        IReadOnlyList<TranslationTask> loaded;
        try
        {
            loaded = _store.Load() ?? Array.Empty<TranslationTask>();
        }
        catch (Exception ex)
        {
            // JsonTaskStore 自己已经兜了一层；这里再兜一层，防止别的 ITaskStore 实现抛出来。
            Log.Warning(ex, "任务恢复失败，本次以空队列启动");
            return;
        }

        lock (_gate)
        {
            foreach (var task in loaded)
            {
                if (task == null) continue;
                NormalizeRestoredTask(task);
                _tasks.Add(task);
                if (!task.IsFinished) _pendingFromLastRun.Add(task);
            }
        }

        if (_pendingFromLastRun.Count > 0)
            Log.Information("检测到上次运行遗留 {Count} 个未完成任务，等待用户决定是否继续", _pendingFromLastRun.Count);
    }

    private List<TranslationTask> TakeRunnableQueue()
    {
        lock (_gate)
        {
            return _tasks
                .Where(t => t.Status == TranslationTaskStatus.Pending)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.CreatedAt)
                .ToList();
        }
    }

    private void MarkStarted(TranslationTask task)
    {
        lock (_gate)
        {
            task.StartedAt = DateTime.UtcNow;
            task.CompletedAt = null;
            task.Error = null;
            task.LastExportPath = null;
            task.OutputPath = null;
            task.Progress = 0;
            task.TextCount = 0;
            task.TranslatedCount = 0;
            task.FailedCount = 0;
            task.AutoScaledLabelCount = 0;
            task.InterferenceResolvedCount = 0;
            task.ReviewCompletedAt = null;
            task.LastExportedAt = null;
            TouchTask(task, task.StartedAt.Value);
        }
    }

    private void SetStatus(TranslationTask task, TranslationTaskStatus status)
    {
        lock (_gate)
        {
            // Pause can race with the tiny gap between WaitWhilePausedAsync and this stage update.
            // Keep the row visibly paused and remember the stage to restore instead of throwing an
            // illegal Paused -> active transition or falsely presenting work as resumed.
            if (task.Status == TranslationTaskStatus.Paused)
            {
                _statusBeforePause[task.Id] = status;
                return;
            }

            var reason = status switch
            {
                TranslationTaskStatus.Parsing => TranslationTaskTransitionReason.PipelineStarted,
                TranslationTaskStatus.Extracting => TranslationTaskTransitionReason.ExtractionStarted,
                TranslationTaskStatus.Translating => TranslationTaskTransitionReason.TranslationStarted,
                TranslationTaskStatus.LayoutOptimizing => TranslationTaskTransitionReason.LayoutOptimizationStarted,
                TranslationTaskStatus.Writing => TranslationTaskTransitionReason.WritingStarted,
                _ => throw new InvalidOperationException($"Pipeline cannot enter {status} through SetStatus.")
            };
            task.TransitionTo(status, reason);
        }
        RaiseTaskUpdated(task);
        RaiseOverallProgress();
        SaveThrottled();
    }

    private void Finish(TranslationTask task, string note)
    {
        lock (_gate)
        {
            var completedAt = DateTime.UtcNow;
            var target = task.FailedCount > 0
                ? TranslationTaskStatus.PartiallyCompleted
                : TranslationTaskStatus.Completed;
            var reason = task.FailedCount > 0
                ? TranslationTaskTransitionReason.CompletedWithFailures
                : TranslationTaskTransitionReason.CompletedWithoutContent;
            task.TransitionTo(target, reason, completedAt);
            task.Progress = 100;
            task.CompletedAt = completedAt;
        }

        RaiseTaskUpdated(task);
        RaiseProgressMessage(Stage(task, StageCount, note));
        RaiseOverallProgress();
        SaveNow();
        ReleaseTaskMemory(task);

        Log.Information("任务完成：{File}（{Translated} 条译文，{Failed} 条失败，耗时 {Elapsed}）",
            task.FileName, task.TranslatedCount, task.FailedCount, task.ElapsedText);
    }

    private void MarkFailed(TranslationTask task, Exception ex)
    {
        lock (_gate)
        {
            var failedAt = DateTime.UtcNow;
            task.TransitionTo(TranslationTaskStatus.Failed, TranslationTaskTransitionReason.Failed, failedAt);
            task.CompletedAt = failedAt;
            task.Error = ex.Message;
        }

        // 记 Error 日志：任务失败是"这张图纸有问题"，不是"队列崩了"，
        // 所以异常到此为止，不再往上抛。
        Log.Error(ex, "任务失败：{File}", task.FileName);
        RaiseTaskUpdated(task);
        RaiseProgressMessage(Stage(task, StageCount, $"失败：{ex.Message}"));
        RaiseOverallProgress();
        SaveNow();
        ReleaseTaskMemory(task);
    }

    private void MarkCancelled(TranslationTask task)
    {
        lock (_gate)
        {
            var cancelledAt = DateTime.UtcNow;
            task.TransitionTo(TranslationTaskStatus.Cancelled, TranslationTaskTransitionReason.CancelledByUser, cancelledAt);
            task.CompletedAt = cancelledAt;
        }

        Log.Information("任务已取消：{File}", task.FileName);
        RaiseTaskUpdated(task);
        RaiseProgressMessage(Stage(task, StageCount, "已取消"));
        RaiseOverallProgress();
        SaveNow();
    }

    /// <summary>
    /// 暂停闸门。读 _paused 与取闸门在同一把锁里，避免"看到暂停却没有闸门可等"导致空转。
    /// 取消时也要能立刻解开，所以等待用 WhenAny 而不是阻塞式 Wait。
    /// </summary>
    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? gate;
        lock (_gate)
        {
            if (!_paused) return;
            gate = _pauseTcs?.Task;
        }

        if (gate == null) return;

        var abort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => abort.TrySetResult(true)))
        {
            await Task.WhenAny(gate, abort.Task).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 大图纸的实体列表是内存峰值来源；每张图纸结束后按配置提示回收一次，
    /// 用 Optimized 模式让 GC 在判定无收益时直接忽略请求，不做阻塞式全代回收。
    /// </summary>
    private void ReleaseTaskMemory(TranslationTask task)
    {
        if (!Options.MemoryOptimization) return;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
        Log.Debug("任务 {File} 结束后已请求释放大对象", task.FileName);
    }

    /// <summary>
    /// 整体进度：已完成的任务算满，进行中的按自己的百分比折算；
    /// 已取消的不进分母（取消不是"没做完"，算进去会让进度永远到不了 100）。
    /// </summary>
    private double ComputeOverallProgress()
    {
        lock (_gate)
        {
            double sum = 0;
            int count = 0;

            foreach (var task in _tasks)
            {
                if (task.Status == TranslationTaskStatus.Cancelled) continue;
                sum += task.IsFinished ? 100 : task.Progress;
                count++;
            }

            if (count == 0) return 0;
            return Math.Min(100, Math.Max(0, sum / count));
        }
    }

    private int CountBy(TranslationTaskStatus status)
    {
        lock (_gate) return _tasks.Count(t => t.Status == status);
    }

    /// <summary>阶段化日志行：<c>[2/6] 液压系统图.dwg 正在翻译文本... 65%</c>。</summary>
    private static string Stage(TranslationTask task, int index, string text, double? percent = null) =>
        percent.HasValue
            ? $"[{index}/{StageCount}] {task.FileName} {text}... {percent.Value:0}%"
            : $"[{index}/{StageCount}] {task.FileName} {text}";

    private void RaiseTaskUpdated(TranslationTask task)
    {
        // 订阅方（UI）抛异常不能反噬队列，所以每个事件都单独兜住。
        try { TaskUpdated?.Invoke(this, task); }
        catch (Exception ex) { Log.Debug(ex, "TaskUpdated 订阅方异常已忽略"); }
    }

    private void RaiseProgressMessage(string message)
    {
        Log.Information("{TaskProgress}", message);
        try { ProgressMessage?.Invoke(this, message); }
        catch (Exception ex) { Log.Debug(ex, "ProgressMessage 订阅方异常已忽略"); }
    }

    private void RaiseOverallProgress()
    {
        var value = ComputeOverallProgress();
        try { OverallProgressChanged?.Invoke(this, value); }
        catch (Exception ex) { Log.Debug(ex, "OverallProgressChanged 订阅方异常已忽略"); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TaskManager));
    }
}

/// <summary>
/// 排版统计探针。
///
/// 为什么需要它：<see cref="CadWriteResult"/> 只带成功/失败条数，写回服务不返回
/// "缩了多少次字号、解了多少次干涉"。这两个数字目前只存在于写回过程的日志文本里
/// （CAD 插件写 cad_plugin_*.jsonl，离线写回写静态 Serilog）。任务卡片要按图纸显示它们，
/// 于是这里挂一个只读的 Serilog sink，把写回期间出现的排版事件记到发起写回的那张图纸上。
///
/// 为什么用 AsyncLocal 而不是共享计数：LocalWorkerCount &gt; 1 时多张图纸同时在写回，
/// 只有"发起这次写回的那条异步流"产生的日志才算它的（异步流内部的 Task.Run 会继承上下文，
/// 别的图纸的流看不到这个计数器）。
///
/// 挂载方式：新 logger → 探针 sink → 宿主原有 logger。原有 sink 一个都不丢，
/// 探针只是"多听一耳朵"；Dispose 时若自己仍是当前 logger 就还原回去。
///
/// 已知边界：AutoCAD 插件在自己的宿主进程里写日志，跨进程那部分不计入本探针，
/// 这种情况下这两个计数保持 0——它们是增量统计，不是图纸的固有属性。
/// </summary>
internal sealed class LayoutStatsProbe : IDisposable
{
    /// <summary>写回引擎标注"缩小字号塞进包络"的日志文案（见 AcadWriterEngine / 插件日志口径）。</summary>
    private const string AutoScaleFragment = "uniform scale to";

    /// <summary>写回引擎标注"靠缩小解决干涉"的日志文案。</summary>
    private const string InterferenceFragment = "resolved by shrinking";

    private static readonly AsyncLocal<Counter?> Current = new();
    private static readonly object InstallGate = new();

    private readonly ILogger? _previousLogger;
    private readonly ILogger? _installedLogger;
    private bool _disposed;

    public LayoutStatsProbe()
    {
        try
        {
            lock (InstallGate)
            {
                var previous = Log.Logger;
                _previousLogger = previous;

                // MinimumLevel.Verbose 是必须的：LoggerConfiguration 默认最低级别是 Information，
                // 而新 logger 会顶替 Log.Logger 成为整个进程的入口，不放开就会把宿主的
                // Debug/Verbose 日志静默吃掉。转发给 previous 的事件仍由 previous 自己的
                // 级别开关过滤，所以宿主实际看到的日志级别没有变化。
                var installed = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Sink(new CountingSink(this), LogEventLevel.Verbose)
                    .WriteTo.Logger(previous, LogEventLevel.Verbose)
                    .CreateLogger();

                _installedLogger = installed;
                Log.Logger = installed;
            }
        }
        catch (Exception ex)
        {
            // 探针只是统计：装不上就让它统计不到，绝不影响写回本身。
            Log.Debug(ex, "排版统计探针安装失败，AutoScaledLabelCount / InterferenceResolvedCount 将保持 0");
        }
    }

    /// <summary>开始统计当前异步流的排版事件（每次写回前调用一次）。</summary>
    public void Begin() => Current.Value = new Counter();

    /// <summary>结束统计并取回结果。</summary>
    public LayoutStats End()
    {
        var counter = Current.Value;
        Current.Value = null;
        return counter == null
            ? default
            : new LayoutStats(counter.AutoScaled, counter.InterferenceResolved);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (InstallGate)
        {
            // 只在自己仍是当前 logger 时还原：宿主中途重配过 Serilog 就别倒着覆盖回去。
            if (_installedLogger != null && ReferenceEquals(Log.Logger, _installedLogger) && _previousLogger != null)
                Log.Logger = _previousLogger;
        }

        // 不释放新 logger：它链着宿主原有的 sink，释放它可能把宿主的文件日志一起关掉。
    }

    private void OnLogEvent(LogEvent logEvent)
    {
        var counter = Current.Value;
        if (counter == null) return; // 不是写回流程里的日志，不统计

        string template;
        try { template = logEvent.MessageTemplate.Text; }
        catch (Exception ex) { Log.Debug(ex, "排版探针读取日志模板失败"); return; }

        if (template.IndexOf(AutoScaleFragment, StringComparison.Ordinal) >= 0)
            counter.AutoScaled++;
        else if (template.IndexOf(InterferenceFragment, StringComparison.Ordinal) >= 0)
            counter.InterferenceResolved++;
    }

    /// <summary>本异步流的计数器；只有发起写回的那条流看得到它。</summary>
    private sealed class Counter
    {
        public int AutoScaled;
        public int InterferenceResolved;
    }

    private sealed class CountingSink : ILogEventSink
    {
        private readonly LayoutStatsProbe _owner;

        public CountingSink(LayoutStatsProbe owner) => _owner = owner;

        public void Emit(LogEvent logEvent)
        {
            // 统计用的 sink 永远不能把日志管道搞崩。
            try { _owner.OnLogEvent(logEvent); }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>一次写回期间的排版统计。</summary>
    public readonly struct LayoutStats
    {
        public LayoutStats(int autoScaled, int interferenceResolved)
        {
            AutoScaled = autoScaled;
            InterferenceResolved = interferenceResolved;
        }

        /// <summary>为放入可用空间而缩小字号的次数。</summary>
        public int AutoScaled { get; }

        /// <summary>通过缩小字号解决干涉的次数。</summary>
        public int InterferenceResolved { get; }
    }
}
