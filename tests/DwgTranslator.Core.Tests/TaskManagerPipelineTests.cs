using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;
using DwgTranslator.Core.Translation;
using Xunit;

namespace DwgTranslator.Core.Tests;

/// <summary>
/// 任务层的端到端行为锁（流水线 + 命名策略 + 故障隔离），用替身把 CAD 读写与 AI 调用换掉：
/// 真实验证的是"任务层自己该负责的事"——阶段推进、进度回填、写回路径、单张失败不影响其它。
///
/// 这些断言对应 UI 侧看到的东西：任务表里的状态/进度/耗时、写回后的 OutputPath、以及
/// <c>TranslationCompleted</c> 事件（界面用它同步文字条目表）。
/// </summary>
public class TaskManagerPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly string _exportDir;

    public TaskManagerPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dwgc2e-task-" + Guid.NewGuid().ToString("N")[..8]);
        _exportDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_exportDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private string CreateDrawing(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "fake dwg");
        return path;
    }

    [Fact]
    public void SavedConfigurationReachesTaskSnapshotOnlyWhenApplied()
    {
        var snapshot = Config();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), new InMemoryTaskStore(), snapshot, out _);
        var draft = Config(); draft.ProtectModels = !snapshot.ProtectModels; draft.OutputNamingPattern = "{name}_changed";
        Assert.NotEqual(draft.ProtectModels, snapshot.ProtectModels);
        manager.ApplyConfiguration(draft);
        Assert.Equal(draft.ProtectModels, snapshot.ProtectModels);
        Assert.Equal("{name}_changed", snapshot.OutputNamingPattern);
        draft.OutputNamingPattern = "later-edit";
        Assert.Equal("{name}_changed", snapshot.OutputNamingPattern);
    }

    private AppConfig Config() => new()
    {
        ExportDirectory = _exportDir,
        OutputNamingPattern = "{name}_{lang}",
        DuplicatePolicy = "skip",
        BackupSourceBeforeWrite = false,
        AllowOverwriteSource = false
    };

    private static TaskManagerOptions Options() => new()
    {
        LocalWorkerCount = 1,
        AiConcurrency = 1,
        MaxRetryCount = 0,
        MemoryOptimization = false
    };

    private sealed class InMemoryTaskStore : ITaskStore
    {
        public List<TranslationTask> Saved { get; } = new();
        public IReadOnlyList<TranslationTask> Load() => Saved;
        public void Save(IEnumerable<TranslationTask> tasks)
        {
            Saved.Clear();
            Saved.AddRange(tasks);
        }
        public void Clear() => Saved.Clear();
    }

    private sealed class FakeReader : IDwgReaderService
    {
        private readonly bool _fail;
        public FakeReader(bool fail = false) { _fail = fail; }

        public List<TextEntity> ExtractFromFile(string filePath)
        {
            if (_fail) throw new InvalidOperationException("模拟解析失败：图纸损坏");
            return new List<TextEntity>
            {
                new() { Handle = "A1", PlainText = "表面粗糙度", RawText = "表面粗糙度" },
                new() { Handle = "A2", PlainText = "倒角", RawText = "倒角" }
            };
        }

        public Dictionary<string, List<TextEntity>> ExtractFromFiles(IEnumerable<string> filePaths)
            => filePaths.ToDictionary(p => p, ExtractFromFile);
    }

    /// <summary>替身翻译：改实体状态（真实 TranslationService 也这么做，写回集合依赖它）。</summary>
    private sealed class FakeTranslator : ITranslationService
    {
        public int Calls { get; private set; }

        public Task<List<TranslationPair>> TranslateBatchAsync(
            List<TextEntity> entities, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
            => TranslateBatchWithProgressAsync(entities, sourceLang, targetLang, null, cancellationToken);

        public Task<string> TranslateAsync(
            string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
            => Task.FromResult($"[{targetLang}]{text}");

        public Task<List<TranslationPair>> TranslateBatchWithProgressAsync(
            List<TextEntity> entities, string sourceLang, string targetLang,
            IProgress<TranslationPair>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var pairs = new List<TranslationPair>();
            foreach (var entity in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 必须是"真正的译文"：导出前的质检会拒绝仍包含原文的结果（TaskManager.BuildWritebackSet），
                // 用 [EN]原文 这种拼接会被正确拦下，所以这里给出对应的英文。
                var translated = entity.PlainText switch
                {
                    "表面粗糙度" => "Surface Roughness",
                    "倒角" => "Chamfer",
                    _ => "Translated Text"
                };
                entity.TranslatedText = translated;
                entity.Status = TranslationStatus.Translated;

                var pair = new TranslationPair
                {
                    Handle = entity.Handle,
                    SourceFilePath = entity.SourceFilePath,
                    SourceText = entity.PlainText,
                    TranslatedText = translated,
                    Status = TranslationStatus.Translated
                };
                pairs.Add(pair);
                progress?.Report(pair);
            }
            return Task.FromResult(pairs);
        }
    }

    private sealed class FakeWriter : IDwgWriterService
    {
        public List<string> Written { get; } = new();

        public CadWriteResult WriteTranslations(
            string sourceFilePath, string outputFilePath, List<TextEntity> entities,
            bool targetIsCjk = true, CancellationToken cancellationToken = default, WritebackOptions? options = null)
        {
            Written.Add(outputFilePath);
            File.WriteAllText(outputFilePath, "written");
            return new CadWriteResult { SuccessCount = entities.Count, FailCount = 0 };
        }
    }

    private TaskManager CreateManager(
        IDwgReaderService reader, ITranslationService translator, IDwgWriterService writer,
        ITaskStore store, AppConfig config, out List<TranslationPair> completed)
    {
        var manager = new TaskManager(reader, null, translator, writer, null, store, Options(), config);
        var pairs = new List<TranslationPair>();
        manager.TranslationCompleted += (_, pair) => pairs.Add(pair);
        completed = pairs;
        return manager;
    }

    [Fact]
    public async Task Pipeline_WritesOutput_WithConfiguredNaming_AndReportsProgress()
    {
        var source = CreateDrawing("motor.dwg");
        var translator = new FakeTranslator();
        var writer = new FakeWriter();

        using var manager = CreateManager(new FakeReader(), translator, writer, new InMemoryTaskStore(), Config(), out var completed);
        var task = manager.Enqueue(source);
        await manager.RunAsync();

        Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        Assert.Equal(2, task.TextCount);
        Assert.Equal(2, task.TranslatedCount);
        Assert.Equal(0, task.FailedCount);
        Assert.Equal(100, task.Progress, 1);

        // 命名规则按配置生效：motor.dwg → motor_en.dwg（目标语言 EN）
        Assert.NotNull(task.OutputPath);
        Assert.Equal("motor_en.dwg", Path.GetFileName(task.OutputPath!));
        Assert.True(File.Exists(task.OutputPath!));
        Assert.Equal(task.OutputPath, writer.Written.Single());

        // 界面靠这个事件同步文字条目表：两条译文都要回传，且带来源图纸
        Assert.Equal(2, completed.Count);
        Assert.All(completed, p => Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(p.SourceFilePath)));
    }

    [Fact]
    public async Task Pipeline_OneBadDrawing_DoesNotStopTheOthers()
    {
        var good = CreateDrawing("good.dwg");
        var bad = CreateDrawing("bad.dwg");
        var store = new InMemoryTaskStore();

        // 同一个 reader 按路径区分成败：bad.dwg 抛异常
        var reader = new RoutedReader(bad);
        var writer = new FakeWriter();
        using var manager = CreateManager(reader, new FakeTranslator(), writer, store, Config(), out _);

        var goodTask = manager.Enqueue(good);
        var badTask = manager.Enqueue(bad);
        await manager.RunAsync();

        Assert.Equal(TranslationTaskStatus.Completed, goodTask.Status);
        Assert.Equal(TranslationTaskStatus.Failed, badTask.Status);
        Assert.Contains("模拟解析失败", badTask.Error);
        Assert.Single(writer.Written);
        Assert.Equal("good_en.dwg", Path.GetFileName(writer.Written[0]));

        // 队列结束后状态应当已持久化（UI 依赖它做"未完成任务续跑"）
        Assert.Contains(store.Saved, t => t.Id == goodTask.Id && t.Status == TranslationTaskStatus.Completed);
    }

    [Fact]
    public async Task Pipeline_ExistingOutput_IsSkipped_NotOverwritten()
    {
        var source = CreateDrawing("flange.dwg");
        // 目标文件已存在 → 默认重名策略 skip：任务应失败并说明原因，且旧文件内容不被覆盖
        var existing = Path.Combine(_exportDir, "flange_en.dwg");
        File.WriteAllText(existing, "previous result");

        var translator = new FakeTranslator();
        using var manager = CreateManager(new FakeReader(), translator, new FakeWriter(), new InMemoryTaskStore(), Config(), out _);
        var task = manager.Enqueue(source);
        await manager.RunAsync();

        Assert.Equal(TranslationTaskStatus.Skipped, task.Status);
        Assert.Equal(0, translator.Calls);
        Assert.Contains("已存在", task.Error);
        Assert.Equal("previous result", File.ReadAllText(existing));
    }

    [Fact]
    public void ConfigureRun_UpdatesLanguagePairAndOutputDirectory()
    {
        var config = Config();
        config.SourceLanguage = "ZH";
        config.TargetLanguage = "EN";

        using var manager = new TaskManager(new FakeReader(), null, new FakeTranslator(), new FakeWriter(), null,
            new InMemoryTaskStore(), Options(), config);

        Assert.Equal("ZH", manager.SourceLanguage);
        Assert.Equal("EN", manager.TargetLanguage);

        manager.ConfigureRun("EN", "zh-tw", Path.Combine(_root, "other"), TaskWritebackMode.AutoCad);

        Assert.Equal("EN", manager.SourceLanguage);
        Assert.Equal("ZH-TW", manager.TargetLanguage);   // 规范化
        Assert.Equal(Path.Combine(_root, "other"), manager.OutputDirectory);
        Assert.Equal(TaskWritebackMode.AutoCad, manager.WritebackMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressReport_UpdatesCountsAndPublishesBeforeReturning(bool failed)
    {
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(),
            new InMemoryTaskStore(), Config(), out var completed);
        var task = manager.Enqueue(CreateDrawing("progress.dwg"));
        var context = new HoldingContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var method = typeof(TaskManager).GetMethod("BuildTranslationProgress",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var progress = (IProgress<TranslationPair>)method.Invoke(manager, new object[] { task, 1 })!;
            progress.Report(new TranslationPair
            {
                Handle = "A1", SourceFilePath = task.FilePath,
                Status = failed ? TranslationStatus.TranslationFailed : TranslationStatus.Translated,
                TranslatedText = failed ? "" : "Chamfer"
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.Equal(0, context.PostCount);
        Assert.Equal(failed ? 0 : 1, task.TranslatedCount);
        Assert.Equal(failed ? 1 : 0, task.FailedCount);
        Assert.Equal(100, task.Progress);
        Assert.Single(completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPipeline_RoutesHttpResultToWritebackOrFailure(bool unauthorized)
    {
        using var http = new System.Net.Http.HttpClient(new WorkerHandler(unauthorized));
        var api = new DwgTranslator.Core.Api.WorkerApiClient(http, "https://worker.invalid",
            () => "test-session", "test-device", "test-host");
        var config = Config();
        var adapter = new WorkerTranslationService(api, config, (text, raw) => text);
        var writer = new FakeWriter();
        using var manager = CreateManager(new FakeReader(), adapter, writer, new InMemoryTaskStore(),
            config, out var completed);
        manager.ConfigureRun("ZH", "EN");
        var task = manager.Enqueue(CreateDrawing("worker.dwg"));
        await manager.RunAsync();
        if (unauthorized)
        {
            Assert.Equal(TranslationTaskStatus.Failed, task.Status);
            Assert.Contains("token_expired", task.Error);
            Assert.Empty(writer.Written);
            Assert.Empty(completed);
        }
        else
        {
            Assert.Equal(TranslationTaskStatus.Completed, task.Status);
            Assert.Equal(2, task.TranslatedCount);
            Assert.Single(writer.Written);
            Assert.Equal(2, completed.Count);
            Assert.All(completed, p => Assert.Equal(task.FilePath, p.SourceFilePath));
        }
    }

    [Fact]
    public async Task PartialRetryReusesCheckpointAndPreservesPriorOutput()
    {
        using var handler = new PartialHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var api = new DwgTranslator.Core.Api.WorkerApiClient(http, "https://worker.invalid", () => "session", "device", "host");
        var config = Config();
        using var manager = CreateManager(new FakeReader(), new WorkerTranslationService(api, config, (t,r) => t), new FakeWriter(), new InMemoryTaskStore(), config, out _);
        manager.ConfigureRun("ZH", "EN");
        var task = manager.Enqueue(CreateDrawing("partial.dwg"));
        await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.PartiallyCompleted, task.Status);
        var first = task.OutputPath!;
        await manager.RetryFailedAsync();
        Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        Assert.Equal(new[] { 2, 1 }, handler.Counts);
        Assert.NotEqual(first, task.OutputPath);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(task.OutputPath));
    }

    private sealed class PartialHandler : System.Net.Http.HttpMessageHandler
    {
        public List<int> Counts { get; } = new();
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Counts.Add(body.RootElement.GetProperty("items").GetArrayLength());
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(Counts.Count == 1
                ? "{\"success\":true,\"items\":[{\"id\":0,\"translated_text\":\"Surface Roughness\"},{\"id\":1,\"error_code\":\"missing_result\"}]}"
                : "{\"success\":true,\"items\":[{\"id\":0,\"translated_text\":\"Chamfer\"}]}") };
        }
    }

    private sealed class WorkerHandler(bool unauthorized) : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/v1/translate", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-session", request.Headers.Authorization!.Parameter);
            Assert.True(request.Headers.Contains("Idempotency-Key"));
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("ZH", body.RootElement.GetProperty("source_lang").GetString());
            var json = unauthorized ? """{"error_code":"token_expired"}""" :
                """{"success":true,"items":[{"id":0,"translated_text":"Surface Roughness"},{"id":1,"translated_text":"Chamfer"}]}""";
            return new System.Net.Http.HttpResponseMessage(unauthorized ? System.Net.HttpStatusCode.Unauthorized : System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
    private sealed class HoldingContext : SynchronizationContext
    {
        public int PostCount { get; private set; }
        public override void Post(SendOrPostCallback callback, object? state) => PostCount++;
    }
    /// <summary>按路径决定成功/失败的 reader 替身。</summary>
    private sealed class RoutedReader(string failingPath) : IDwgReaderService
    {
        public List<TextEntity> ExtractFromFile(string filePath)
        {
            if (string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(failingPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("模拟解析失败：图纸损坏");
            return new FakeReader().ExtractFromFile(filePath);
        }

        public Dictionary<string, List<TextEntity>> ExtractFromFiles(IEnumerable<string> filePaths)
            => filePaths.ToDictionary(p => p, ExtractFromFile);
    }
}


