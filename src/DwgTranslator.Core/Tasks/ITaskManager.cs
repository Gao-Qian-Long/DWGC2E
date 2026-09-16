using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;

namespace DwgTranslator.Core.Tasks;

/// <summary>Optional warning about incomplete task recovery.</summary>
public interface ITaskRecoveryDiagnostics
{
    string? RecoveryWarning { get; }
}

/// <summary>Optional save diagnostics for stores that deliberately keep save errors non-fatal.</summary>
public interface ITaskStoreDiagnostics
{
    bool LastSaveFailed { get; }
}

/// <summary>
/// 并发与重试参数。提示词要求"多线程不是无限线程"，因此本地解析/写回与 AI 请求
/// 是两条独立限流：LocalWorkerCount 控制同时在跑的图纸数，AiConcurrency 控制
/// 单张图纸内部的 AI 请求并发。
/// </summary>
public sealed class TaskManagerOptions
{
    /// <summary>本地并发：同时解析 + 写回的图纸数（1..6）。</summary>
    public int LocalWorkerCount { get; set; } = 2;

    /// <summary>AI 并发：单张图纸内的翻译请求并发数（1..8）。</summary>
    public int AiConcurrency { get; set; } = 4;

    /// <summary>失败重试次数（0..5）。</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>内存优化：每张图纸完成后释放大对象。</summary>
    public bool MemoryOptimization { get; set; } = true;
}

/// <summary>
/// 任务持久化。提示词建议 SQLite（本机工作状态），但本机离线环境下无法引入新的
/// NuGet 包，因此先用 JSON 实现（<see cref="JsonTaskStore"/>），接口保持一致，
/// 以后替换成 SQLite 不需要改上层。
/// </summary>
public interface ITaskStore
{
    /// <summary>读取上次运行遗留的任务（用于"检测到未完成任务，是否继续"）。</summary>
    IReadOnlyList<TranslationTask> Load();

    void Save(IEnumerable<TranslationTask> tasks);

    void Clear();
}

/// <summary>
/// 任务层。UI 只通过它提交/取消/重试任务，并订阅事件刷新界面——
/// 按钮事件里不再直接解析 DWG 或请求 AI。
/// </summary>
public interface ITaskManager : IDisposable
{
    IReadOnlyList<TranslationTask> Tasks { get; }

    /// <summary>
    /// 上次运行遗留、可供继续的任务（构造时从 <see cref="ITaskStore"/> 恢复，状态已被重置回等待中）。
    /// UI 启动时用它询问「检测到 N 张图纸未完成，是否继续？」——这些任务已经在 <see cref="Tasks"/> 里，
    /// 但不会自己开跑，只有调用 <see cref="RunAsync"/> 才执行；用户选「不继续」时调
    /// <see cref="Clear"/> 清掉即可。
    /// </summary>
    IReadOnlyList<TranslationTask> PendingFromLastRun { get; }

    TaskManagerOptions Options { get; }

    /// <summary>队列是否处于暂停（按钮文案用）。</summary>
    bool IsPaused { get; }

    /// <summary>是否有队列正在执行（重复 RunAsync 会被忽略）。</summary>
    bool IsRunning { get; }

    /// <summary>输出目录。为空时任务层退回 AppConfig.ExportDirectory（UI 通常在 ConfigureRun 里给绝对值）。</summary>
    string? OutputDirectory { get; set; }

    /// <summary>写回方式：离线（默认）/ AutoCAD。</summary>
    TaskWritebackMode WritebackMode { get; set; }

    /// <summary>某个任务的状态/进度变化（UI 线程外触发，订阅方自行调度）。</summary>
    event EventHandler<TranslationTask>? TaskUpdated;

    /// <summary>阶段化日志：[1/6] 总装配图.dwg 解析完成，共提取 1,284 个文本。</summary>
    event EventHandler<string>? ProgressMessage;

    /// <summary>整体进度变化（0..100）。</summary>
    event EventHandler<double>? OverallProgressChanged;

    /// <summary>
    /// 单条文本翻译完成（含命中术语表 / 本地跳过 / 失败）。
    /// UI 用它同步「文字条目表」与统计卡：任务层才是翻译的执行者，但人工校对与 Excel 复核
    /// 仍然依赖界面上的实体列表，两边状态必须一致。找不到对应实体时订阅方忽略即可。
    /// </summary>
    event EventHandler<TranslationPair>? TranslationCompleted;

    /// <summary>
    /// 启动队列前由 UI 把界面上的运行参数交给任务层（语言对来自语言选择器，输出目录与写回方式
    /// 来自输出设置卡）。传 null 的项表示"沿用任务层当前值"。
    /// </summary>
    void ConfigureRun(string sourceLanguage, string targetLanguage,
        string? outputDirectory = null, TaskWritebackMode? writebackMode = null);

    TranslationTask Enqueue(string filePath, TaskPriority priority = TaskPriority.Normal);

    void EnqueueRange(IEnumerable<string> filePaths, TaskPriority priority = TaskPriority.Normal);

    void Remove(TranslationTask task);

    /// <summary>清空已结束的任务；includeUnfinished 为真时连未完成的也清掉。</summary>
    void Clear(bool includeUnfinished = false);

    /// <summary>按优先级顺序跑完队列（本地并发 + AI 并发双层限流，UI 不被阻塞）。</summary>
    Task RunAsync(CancellationToken cancellationToken = default);

    void PauseAll();

    void ResumeAll();

    void CancelAll();

    Task RetryFailedAsync(CancellationToken cancellationToken = default);

    /// <summary>取消正在执行的队列（不影响已完成的记录）。</summary>
    void CancelCurrentRun();
}
