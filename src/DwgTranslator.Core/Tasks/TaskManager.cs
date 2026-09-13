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
public sealed class TaskManager : ITaskManager
{
    /// <summary>阶段总数：解析 → 提取 → 翻译 → 排版优化 → 写回 → 完成。</summary>
    private const int StageCount = 6;

    private readonly IDwgReaderService _dwgReader;
    private readonly IDxfReaderService? _dxfReader;
    private readonly ITranslationService _translationService;
    private readonly IDwgWriterService _dwgWriter;
    private readonly IDxfWriterService? _dxfWriter;
    private readonly IAutoCadInteropService? _autoCadInterop;
    private readonly ITaskStore _store;
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

    /// <summary>当前队列每个源图纸对应的输出路径（按批量导出规则批量算好，避免同名互相覆盖）。</summary>
    private Dictionary<string, string> _destinations = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 输出目录。Core 不感知 %APPDATA%，所以默认取 AppConfig.ExportDirectory（App 启动时
    /// 已把相对路径解析到数据目录），需要指向别处就由调用方显式赋值。
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>写回方式，默认离线（不依赖 CAD 安装，也不会弹框打断批处理）。</summary>
    public TaskWritebackMode WritebackMode { get; set; } = TaskWritebackMode.Offline;

    /// <summary>本队列的语言对（已规范化）。UI 只读用于日志与显示，修改请走 <see cref="ConfigureRun"/>。</summary>
    public string SourceLanguage => _sourceLanguage;

    public string TargetLanguage => _targetLanguage;

    /// <inheritdoc/>
    public void ConfigureRun(string sourceLanguage, string targetLanguage,
        string? outputDirectory = null, TaskWritebackMode? writebackMode = null)
    {
        ThrowIfDisposed();

        _sourceLanguage = TranslationLanguages.Normalize(sourceLanguage);
        _targetLanguage = TranslationLanguages.Normalize(targetLanguage);

        if (!string.IsNullOrWhiteSpace(outputDirectory)) OutputDirectory = outputDirectory;
        if (writebackMode.HasValue) WritebackMode = writebackMode.Value;

        Log.Information("任务层运行参数：{Source} → {Target}，输出目录 {Folder}，写回方式 {Mode}",
            _sourceLanguage, _targetLanguage, OutputDirectory ?? "(配置默认)", WritebackMode);
    }

    /// <inheritdoc/>
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
        SaveNow();
        Log.Information("任务入队：{File}（优先级 {Priority}，队列 {Count}）", task.FileName, task.PriorityText, Tasks.Count);
        return task;
    }

    /// <inheritdoc/>
    public void EnqueueRange(IEnumerable<string> filePaths, TaskPriority priority = TaskPriority.Normal)
    {
        ThrowIfDisposed();
        if (filePaths == null) return;

        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                Enqueue(path, priority);
            }
            catch (Exception ex)
            {
                // 批量拖入时一个坏路径不该让整批都进不去队列。
                Log.Warning(ex, "图纸入队失败，已跳过：{Path}", path);
            }
        }
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
    public async Task RunAsync(CancellationToken cancellationToken = default)
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

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _runCts;
            _running = true;
        }

        try
        {
            var queue = TakeRunnableQueue();
            if (queue.Count == 0)
            {
                Log.Information("队列里没有待处理的图纸");
                return;
            }

            PrepareDestinations();
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
                task.Status = TranslationTaskStatus.Paused;
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
                task.Status = previous == TranslationTaskStatus.Paused ? TranslationTaskStatus.Pending : previous;
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
                if (task.Status == TranslationTaskStatus.Paused) task.Status = TranslationTaskStatus.Pending;
                // 正在执行的图纸交给取消令牌（它会走到取消分支并置 Cancelled）；
                // 已经结束的记录保持原样，取消不该抹掉历史。
                if (task.Status != TranslationTaskStatus.Pending) continue;

                task.Status = TranslationTaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
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
            retried = _tasks.Where(t => t.Status == TranslationTaskStatus.Failed).ToList();
            foreach (var task in retried)
            {
                task.Status = TranslationTaskStatus.Pending;
                task.Progress = 0;
                task.TextCount = 0;
                task.TranslatedCount = 0;
                task.FailedCount = 0;
                task.AutoScaledLabelCount = 0;
                task.InterferenceResolvedCount = 0;
                task.Error = null;
                task.StartedAt = null;
                task.CompletedAt = null;
                task.OutputPath = null;
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

    // ───────────────────────────── 单张图纸流水线 ─────────────────────────────

    /// <summary>
    /// 跑完一张图纸的 6 个阶段。任何一步抛异常都只影响这一张图纸：
    /// 置 Failed + 写 Error，然后正常归还槽位，队列继续下一张。
    /// </summary>
    private async Task ProcessTaskAsync(TranslationTask task, SemaphoreSlim workerGate, CancellationToken ct)
    {
        try
        {
            // 暂停时停在阶段边界：这张图纸还没占用 AI 配额，恢复后从当前阶段继续。
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            MarkStarted(task);

            // ── [1/6] Parsing：读 DWG/DXF ──
            SetStatus(task, TranslationTaskStatus.Parsing);
            RaiseProgressMessage(Stage(task, 1, "正在解析图纸"));
            var entities = await Task.Run(() => ReadTextEntities(task), ct).ConfigureAwait(false);

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
                var pairs = await _translationService
                    .TranslateBatchWithProgressAsync(
                        toTranslate, _sourceLanguage, _targetLanguage, progress, ct)
                    .ConfigureAwait(false);

                Log.Information("图纸 {File}：翻译阶段结束，返回 {Pairs} 条译文", task.FileName, pairs?.Count ?? 0);
            }

            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            // ── [4/6] LayoutOptimizing：圈定写回范围 + 算输出路径 ──
            SetStatus(task, TranslationTaskStatus.LayoutOptimizing);
            RaiseProgressMessage(Stage(task, 4, "正在优化排版"));

            var writebackSet = BuildWritebackSet(task, entities);
            if (writebackSet.Count == 0)
            {
                if (task.FailedCount > 0)
                    throw new InvalidOperationException("没有可写回的译文，所有待翻译条目均失败");
                Finish(task, "没有可写回的译文");
                return;
            }

            var outputPath = ResolveOutputPath(task.FilePath);

            await WaitWhilePausedAsync(ct).ConfigureAwait(false);

            // ── [5/6] Writing：写回（排版收缩/解干涉就发生在这次调用内部） ──
            SetStatus(task, TranslationTaskStatus.Writing);
            RaiseProgressMessage(Stage(task, 5, "正在写回图纸"));

            // 排版统计探针只在这次写回期间统计本异步流产生的事件（见 LayoutStatsProbe 注释）。
            _layoutProbe.Begin();
            CadWriteResult? result = null;
            try
            {
                result = await WritebackAsync(task.FilePath, outputPath, writebackSet, ct).ConfigureAwait(false);
            }
            finally
            {
                var stats = _layoutProbe.End();
                lock (_gate)
                {
                    task.AutoScaledLabelCount += stats.AutoScaled;
                    task.InterferenceResolvedCount += stats.InterferenceResolved;
                }
            }

            if (result == null || result.SuccessCount <= 0)
            {
                var detail = result != null && result.Errors.Count > 0
                    ? string.Join("; ", result.Errors.Take(3))
                    : "写回过程没有写入任何文本";
                throw new InvalidOperationException(detail);
            }

            if (result.FailCount == 0 && result.SuccessCount >= writebackSet.Count)
            {
                foreach (var entity in writebackSet)
                    entity.Status = TranslationStatus.WritebackSuccess;
            }

            lock (_gate)
            {
                task.OutputPath = outputPath;
                task.FailedCount += result.FailCount;
            }

            if (result.FailCount > 0)
                RaiseProgressMessage(Stage(task, 5, $"{result.SuccessCount} 条已写回，{result.FailCount} 条未写入"));

            SaveNow();

            // ── [6/6] Completed ──
            Finish(task, $"完成：{task.TranslatedCount:N0} 条译文{(task.FailedCount > 0 ? $"，{task.FailedCount:N0} 条失败" : string.Empty)}，输出 {Path.GetFileName(outputPath)}");
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
                    else task.TranslatedCount++;

                    percent = total <= 0 ? 100 : Math.Min(100, (double)done / total * 100.0);
                    task.Progress = percent;
                }

                // TaskUpdated 不节流：UI 需要连续的进度。日志行按 400ms 节流，
                // 否则一张图纸上千条进度会把日志文件淹没。
                RaiseTaskUpdated(task);

                var now = DateTime.UtcNow.Ticks;
                var previous = Interlocked.Read(ref lastMessageTicks);
                if (done >= total || now - previous >= TimeSpan.TicksPerMillisecond * 400)
                {
                    Interlocked.Exchange(ref lastMessageTicks, now);
                    RaiseProgressMessage(Stage(task, 3, "正在翻译文本", percent));
                }

                RaiseOverallProgress();

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

        var invalid = candidates
            .Where(e => !TranslationQualityValidator.IsAcceptable(
                e.PlainText, e.TranslatedText, _sourceLanguage, _targetLanguage))
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
    private async Task<CadWriteResult> WritebackAsync(
        string sourcePath, string outputPath, List<TextEntity> entities, CancellationToken ct)
    {
        // 目标语言是否需要 CJK 字体，与导出流程用的是同一个判断。
        var targetIsCjk = TranslationLanguages.IsCjk(_targetLanguage);

        if (WritebackMode == TaskWritebackMode.AutoCad)
        {
            if (_autoCadInterop != null && _autoCadInterop.IsAutoCADAvailable(_config))
            {
                var progress = new Progress<string>(message =>
                {
                    try { ProgressMessage?.Invoke(this, message); }
                    catch (Exception ex) { Log.Debug(ex, "AutoCAD 写回进度订阅方异常已忽略"); }
                });

                return await _autoCadInterop.WritebackViaAutoCadAsync(
                    sourcePath, outputPath, entities, targetIsCjk, _config, progress, ct).ConfigureAwait(false);
            }

            // 选了 CAD 模式但 CAD 不可用：退回离线写回并记警告，比整批失败更有用。
            Log.Warning("AutoCAD 写回不可用（未安装或未启动），本张图纸退回离线写回：{Path}", sourcePath);
        }

        var isDxf = string.Equals(Path.GetExtension(sourcePath), ".dxf", StringComparison.OrdinalIgnoreCase);
        var dxfWriter = _dxfWriter;

        return await Task.Run(() => isDxf && dxfWriter != null
            ? dxfWriter.WriteTranslations(sourcePath, outputPath, entities, targetIsCjk, ct)
            : _dwgWriter.WriteTranslations(sourcePath, outputPath, entities, targetIsCjk, ct), ct)
            .ConfigureAwait(false);
    }

    // ───────────────────────────── 路径与保存 ─────────────────────────────

    /// <summary>
    /// 队列开始时按批量导出规则一次性算好每张图纸的输出路径，避免同名图纸互相覆盖。
    /// 参与预留的是"列表里出现过的所有图纸"（含已完成的）：它们可能已经写出过同名文件，
    /// 把新图纸映射到同一个路径会把上一次的成果覆盖掉。
    /// </summary>
    private void PrepareDestinations()
    {
        var folder = ResolveOutputFolder();
        var sources = Snapshot().Select(t => t.FilePath);

        // 走配置驱动的规划：命名规则（motor.dwg → motor_zh.dwg）、重名策略（默认跳过）
        // 与源文件保护都由 OutputPathResolver 执行；被跳过的文件记日志，不静默丢弃。
        var plan = BatchExportPlanner.CreateExportPlan(sources, _targetLanguage, _config);
        if (plan.Skipped.Count > 0)
        {
            Log.Warning("任务层按重名策略（{Policy}）跳过 {Count} 个已存在的输出文件：{Reasons}",
                _config.DuplicatePolicy, plan.Skipped.Count,
                string.Join("; ", plan.Skipped.Select(s => s.Reason)));
        }

        lock (_gate) _destinations = plan.Destinations;
    }

    /// <summary>输出目录：调用方显式指定 &gt; AppConfig.ExportDirectory &gt; 进程当前目录。</summary>
    private string ResolveOutputFolder()
    {
        var folder = OutputDirectory;
        if (string.IsNullOrWhiteSpace(folder)) folder = _config.ExportDirectory;
        if (string.IsNullOrWhiteSpace(folder)) folder = Directory.GetCurrentDirectory();
        return folder;
    }

    private string ResolveOutputPath(string sourcePath)
    {
        var fullPath = NormalizePath(sourcePath);

        string? destination = null;
        lock (_gate) _destinations.TryGetValue(fullPath, out destination);

        // 未在批量计划里（例如单文件入队）时，按同一套配置规则计算，保证命名与重名策略一致。
        if (destination == null)
        {
            var planned = BatchExportPlanner.CreateDestination(fullPath, _targetLanguage, _config);
            if (planned.ShouldSkip)
            {
                throw new InvalidOperationException(
                    $"输出文件已存在，按重名策略（{_config.DuplicatePolicy}）跳过：{planned.OutputPath}");
            }
            destination = planned.OutputPath;
        }

        if (string.Equals(NormalizePath(destination), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"输出文件与源文件同名，已停止写回以免覆盖原图：{destination}");

        return destination;
    }

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

    /// <summary>阶段结束 / 入队出队 / 任务结束等关键点立即写盘（失败只记日志，见 JsonTaskStore）。</summary>
    private void SaveNow()
    {
        try
        {
            _store.Save(Snapshot());
            lock (_gate) _lastSaveUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "任务状态保存失败（继续运行）");
        }
    }

    /// <summary>队列快照（加锁复制）。交给 UI 或落盘的都是副本，改动不会影响正在跑的队列。</summary>
    private List<TranslationTask> Snapshot()
    {
        lock (_gate) return _tasks.ToList();
    }

    // ───────────────────────────── 状态与事件 ─────────────────────────────

    /// <summary>
    /// 恢复上次运行遗留的任务。恢复出来的任务只是"显示在列表里、标记为等待"，
    /// 不会自动开跑——用户通过 <see cref="PendingFromLastRun"/> 确认后才调用 RunAsync。
    /// </summary>
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
            task.OutputPath = null;
            task.Progress = 0;
            task.TextCount = 0;
            task.TranslatedCount = 0;
            task.FailedCount = 0;
            task.AutoScaledLabelCount = 0;
            task.InterferenceResolvedCount = 0;
        }
    }

    private void SetStatus(TranslationTask task, TranslationTaskStatus status)
    {
        lock (_gate) task.Status = status;
        RaiseTaskUpdated(task);
        RaiseOverallProgress();
        SaveThrottled();
    }

    private void Finish(TranslationTask task, string note)
    {
        lock (_gate)
        {
            task.Status = task.FailedCount > 0
                ? TranslationTaskStatus.Failed
                : TranslationTaskStatus.Completed;
            task.Progress = 100;
            task.CompletedAt = DateTime.UtcNow;
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
            task.Status = TranslationTaskStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
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
            task.Status = TranslationTaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
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




