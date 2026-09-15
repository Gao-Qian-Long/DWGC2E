using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Serilog;

namespace DwgTranslator.Core.Tasks;

/// <summary>
/// 任务持久化的 JSON 实现（<see cref="ITaskStore"/>）。
///
/// 为什么是 JSON 而不是 SQLite：本机离线环境无法引入新的 NuGet 包，而任务状态只是一份
/// "本机工作状态"，量级是几十条任务，JSON 足够；接口保持不变，以后换 SQLite 不动上层。
///
/// 为什么路径由外部传入：Core 不感知平台路径（%APPDATA% 只有 WPF 壳知道），拼路径会让
/// Core 依赖运行环境，也让单元测试无法指向临时文件。调用方（App）负责给出绝对路径。
///
/// 三处刻意的"不抛异常"设计：状态文件是断点续跑的辅助品，不是业务数据——
/// 文件损坏、被占用、磁盘满都不应该让正在跑的翻译失败。因此读写失败一律只记日志。
/// </summary>
public sealed class JsonTaskStore : ITaskStore, ITaskStoreDiagnostics
{
    /// <summary>
    /// 读选项：容忍 camelCase（settings.json 风格）与 PascalCase 两种写法，
    /// 容忍尾逗号与注释——手工编辑过的文件不至于让整次恢复失败。
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 计算属性（StatusText / Elapsed / IsFinished …）是给 UI 用的投影，没有 setter，
        // 读回来也没有意义；忽略后文件里只剩真正的运行数据。
        IgnoreReadOnlyProperties = true
    };

    /// <summary>写选项与读选项保持同一套契约（大小写不敏感读，缩进写，便于人工排查）。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };

    private readonly string _filePath;
    public bool LastSaveFailed { get; private set; }

    /// <summary>读写互斥：TaskManager 会从工作线程节流保存，UI 线程同时可能读取。</summary>
    private readonly object _gate = new();

    /// <param name="filePath">任务状态文件的绝对路径（目录不存在时会在首次保存时创建）。</param>
    public JsonTaskStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("任务状态文件路径不能为空", nameof(filePath));

        _filePath = Path.GetFullPath(filePath);
    }

    /// <summary>状态文件路径（UI 展示"未完成任务来自哪个文件"时用）。</summary>
    public string FilePath => _filePath;

    /// <inheritdoc/>
    public IReadOnlyList<TranslationTask> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    Log.Debug("没有上次运行的任务状态文件：{Path}", _filePath);
                    return Array.Empty<TranslationTask>();
                }

                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TranslationTask>();

                var tasks = JsonSerializer.Deserialize<List<TranslationTask>>(json, ReadOptions);
                if (tasks == null || tasks.Count == 0) return Array.Empty<TranslationTask>();

                var restored = new List<TranslationTask>(tasks.Count);
                int skipped = 0;
                foreach (var task in tasks)
                {
                    // 缺路径的记录跑不起来，留着只会在队列里显示成一个永远失败的行。
                    if (task == null || string.IsNullOrWhiteSpace(task.FilePath))
                    {
                        skipped++;
                        continue;
                    }

                    // 已完成/已取消是历史记录，保留原状（TranslationTask.ResetForResume 只保住
                    // Completed，所以 Cancelled 要在这里自己跳过，否则会被恢复成待跑）。
                    if (task.Status is TranslationTaskStatus.Completed or TranslationTaskStatus.Cancelled or TranslationTaskStatus.PartiallyCompleted or TranslationTaskStatus.Skipped)
                    {
                        restored.Add(task);
                        continue;
                    }

                    // 进程被杀时留下的 Parsing/Translating 是"假运行态"：重置成 Pending 才能重跑。
                    task.ResetForResume();
                    restored.Add(task);
                }

                if (skipped > 0)
                    Log.Warning("任务状态文件里有 {Count} 条记录缺少文件路径，已忽略：{Path}", skipped, _filePath);

                Log.Information("任务恢复：{Total} 条记录，其中 {Pending} 条未完成（{Path}）",
                    restored.Count, restored.Count(t => !t.IsFinished), _filePath);
                return restored;
            }
            catch (Exception ex)
            {
                // 文件损坏 / 被占用 / 半截 JSON：都只意味着"这次没有可恢复的任务"，
                // 不能让它挡住应用启动，更不该把异常抛到 UI 线程的构造函数里。
                Log.Warning(ex, "任务状态文件读取失败，按空队列启动：{Path}", _filePath);
                return Array.Empty<TranslationTask>();
            }
        }
    }

    /// <inheritdoc/>
    public void Save(IEnumerable<TranslationTask> tasks)
    {
        var list = tasks is null ? new List<TranslationTask>() : tasks.ToList();
        string? tempPath = null;

        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(list, WriteOptions), new UTF8Encoding(false));

                if (File.Exists(_filePath))
                {
                    // Fail closed: never replace a recoverable file using non-atomic copy-overwrite.
                    File.Replace(tempPath, _filePath, null);
                }
                else
                {
                    File.Move(tempPath, _filePath);
                }

                tempPath = null;
                LastSaveFailed = false;
                Log.Debug("任务状态已保存：{Count} 条 -> {Path}", list.Count, _filePath);
            }
        }
        catch (Exception ex)
        {
            LastSaveFailed = true;
            // 保存失败不能影响翻译本身：最坏结果是重启后少了断点，而不是这一批图纸跑不完。
            Log.Warning(ex, "任务状态保存失败：{Path}", _filePath);
        }
        finally
        {
            if (tempPath != null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (Exception cleanupEx) { Log.Debug(cleanupEx, "临时任务文件清理失败：{Path}", tempPath); }
            }
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                File.Delete(_filePath);
                Log.Information("已清除任务状态文件：{Path}", _filePath);
            }
            catch (Exception ex)
            {
                // 文件不存在是正常情况（从没跑过任务），被占用也只是少清理一次。
                Log.Warning(ex, "任务状态文件删除失败：{Path}", _filePath);
            }
        }
    }
}
