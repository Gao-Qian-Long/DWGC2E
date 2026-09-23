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
    public void MarkReviewCompleted_AdvancesOnlyReadyForReviewAndRaisesUpdate()
    {
        var store = new InMemoryTaskStore();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var task = manager.Enqueue(CreateDrawing("review-state.dwg"));
        task.Status = TranslationTaskStatus.ReadyForReview;
        task.Progress = 100;
        var updates = 0;
        manager.TaskUpdated += (_, updated) => { if (updated.Id == task.Id) updates++; };

        var beforeReview = DateTime.UtcNow;
        manager.MarkReviewCompleted(task.Id);
        var afterReview = DateTime.UtcNow;

        Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        Assert.Equal(100, task.Progress);
        Assert.NotNull(task.ReviewCompletedAt);
        Assert.InRange(task.ReviewCompletedAt!.Value, beforeReview, afterReview);
        Assert.Equal(task.ReviewCompletedAt, task.UpdatedAt);
        Assert.True(updates > 0);
        Assert.Contains(store.Saved, saved => saved.Id == task.Id && saved.Status == TranslationTaskStatus.Completed);

        var pending = manager.Enqueue(CreateDrawing("not-reviewed.dwg"));
        Assert.Throws<InvalidOperationException>(() => manager.MarkReviewCompleted(pending.Id));
        Assert.Equal(TranslationTaskStatus.Pending, pending.Status);
    }
    [Fact]
    public void RecordExportPath_RecordsExportAuditAndClearsLegacyPath()
    {
        var store = new InMemoryTaskStore();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var task = manager.Enqueue(CreateDrawing("export-audit.dwg"));
        task.Status = TranslationTaskStatus.Completed;
        task.OutputPath = "legacy-output.dwg";
        var output = Path.Combine(_exportDir, "export-audit-en.dwg");
        File.WriteAllText(output, "exported");

        var before = DateTime.UtcNow;
        manager.RecordExportPath(task.Id, output);
        var after = DateTime.UtcNow;

        Assert.Equal(Path.GetFullPath(output), task.LastExportPath);
        Assert.Null(task.OutputPath);
        Assert.NotNull(task.LastExportedAt);
        Assert.InRange(task.LastExportedAt!.Value, before, after);
        Assert.Equal(task.LastExportedAt, task.UpdatedAt);
    }

    [Fact]
    public void RestoreLegacyTask_BackfillsUpdatedAtFromLatestKnownStage()
    {
        var created = DateTime.UtcNow.AddHours(-2);
        var completed = created.AddMinutes(20);
        var store = new InMemoryTaskStore();
        store.Saved.Add(new TranslationTask(CreateDrawing("legacy-audit.dwg"))
        {
            Status = TranslationTaskStatus.Completed,
            CreatedAt = created,
            UpdatedAt = default,
            CompletedAt = completed
        });

        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var restored = Assert.Single(manager.Tasks);

        Assert.Equal(completed, restored.UpdatedAt);
    }

    [Fact]
    public async Task RetryFailedAsync_IncrementsAuditCounterAndClearsDeliveryState()
    {
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), new InMemoryTaskStore(), Config(), out _);
        manager.ConfigureRun("ZH", "EN");
        var task = manager.Enqueue(CreateDrawing("retry-audit.dwg"));
        task.Status = TranslationTaskStatus.Failed;
        task.Error = "temporary failure";
        task.ReviewCompletedAt = DateTime.UtcNow.AddMinutes(-2);
        task.LastExportedAt = DateTime.UtcNow.AddMinutes(-1);
        task.LastExportPath = Path.Combine(_exportDir, "stale.dwg");

        await manager.RetryFailedAsync();

        Assert.Equal(1, task.RetryCount);
        Assert.Equal(TranslationTaskStatus.ReadyForReview, task.Status);
        Assert.Null(task.ReviewCompletedAt);
        Assert.Null(task.LastExportedAt);
        Assert.Null(task.LastExportPath);
        Assert.True(task.UpdatedAt >= task.StartedAt);
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
        BackupSourceBeforeWrite = false,    };

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

    [Fact]
    public async Task ResumingRecoveredTasks_ConsumesPendingRecoveryMarkers()
    {
        var store = new InMemoryTaskStore();
        var source = CreateDrawing("recovered-pending.dwg");
        store.Saved.Add(new TranslationTask(source) { Status = TranslationTaskStatus.Pending });

        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        Assert.Single(manager.PendingFromLastRun);
        manager.ConfigureRun("ZH", "EN");

        await manager.RunAsync();

        Assert.Empty(manager.PendingFromLastRun);
        Assert.Equal(TranslationTaskStatus.ReadyForReview, Assert.Single(manager.Tasks).Status);
    }

    [Fact]
    public async Task RetryingOneRecoveredTask_ConsumesOnlyItsRecoveryMarker()
    {
        var store = new InMemoryTaskStore();
        var first = new TranslationTask(CreateDrawing("recovered-first.dwg")) { Status = TranslationTaskStatus.Pending };
        var second = new TranslationTask(CreateDrawing("recovered-second.dwg")) { Status = TranslationTaskStatus.Pending };
        store.Saved.Add(first);
        store.Saved.Add(second);

        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var restoredFirst = manager.Tasks.Single(t => t.FilePath == first.FilePath);
        manager.ConfigureRun("ZH", "EN");
        Assert.Equal(2, manager.PendingFromLastRun.Count);

        await manager.RetryTaskAsync(restoredFirst.Id);

        Assert.Equal(TranslationTaskStatus.ReadyForReview, restoredFirst.Status);
        var remaining = Assert.Single(manager.PendingFromLastRun);
        Assert.Equal(second.FilePath, remaining.FilePath);
    }

    [Fact]
    public void FailedAccountSaveKeepsQueueAndDoesNotCommitNewSession()
    {
        var store = new DiagnosticTaskStore();
        var destination = new InMemoryTaskStore();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var first = manager.Enqueue(CreateDrawing("unsaved-owner.dwg"));
        store.LastSaveFailed = true;
        var committed = false;
        Assert.Throws<IOException>(() => manager.SwitchAccountStore(destination, () => committed = true));
        Assert.False(committed);
        Assert.Same(first, Assert.Single(manager.Tasks));
        Assert.Empty(destination.Saved);
        Assert.Throws<IOException>(() => manager.EnsureAccountStoreSaved());
        store.LastSaveFailed = false;
        manager.SwitchAccountStore(destination, () => committed = true);
        Assert.True(committed);
        Assert.Empty(manager.Tasks);
    }

    [Fact]
    public void FailedSessionCommitKeepsOriginalStoreAndPendingQueue()
    {
        var store = new InMemoryTaskStore();
        var destination = new InMemoryTaskStore();
        var previous = new TranslationTask(CreateDrawing("previous-owner.dwg"));
        store.Saved.Add(previous);
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        Assert.Throws<IOException>(() => manager.SwitchAccountStore(destination, () => throw new IOException("settings locked")));
        Assert.Same(previous, Assert.Single(manager.Tasks));
        Assert.Same(previous, Assert.Single(manager.PendingFromLastRun));
        var added = manager.Enqueue(CreateDrawing("still-original-owner.dwg"));
        Assert.Contains(added, store.Saved);
        Assert.Empty(destination.Saved);
        manager.SwitchAccountStore(destination);
        Assert.Empty(manager.Tasks);
        manager.SwitchAccountStore(store);
        Assert.Equal(2, manager.Tasks.Count);
    }

    [Fact]
    public void RepeatedAccountSwitchesKeepBothOwnersSeparate()
    {
        var a = new InMemoryTaskStore(); var b = new InMemoryTaskStore();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), a, Config(), out _);
        var first = manager.Enqueue(CreateDrawing("repeat-alice.dwg"));
        manager.SwitchAccountStore(b);
        var second = manager.Enqueue(CreateDrawing("repeat-bob.dwg"));
        for (var index = 0; index < 50; index++)
        {
            manager.SwitchAccountStore(a);
            Assert.Same(first, Assert.Single(manager.Tasks));
            manager.SwitchAccountStore(b);
            Assert.Same(second, Assert.Single(manager.Tasks));
        }
    }

    private sealed class DiagnosticTaskStore : ITaskStore, ITaskStoreDiagnostics
    {
        public bool LastSaveFailed { get; set; }
        public IReadOnlyList<TranslationTask> Load() => Array.Empty<TranslationTask>();
        public void Save(IEnumerable<TranslationTask> tasks) { }
        public void Clear() { }
    }

    [Fact]
    public void PersistenceWarningsAreDeduplicatedAndReportRecovery()
    {
        var store = new DiagnosticTaskStore { LastSaveFailed = true };
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), store, Config(), out _);
        var messages = new List<string>();
        manager.ProgressMessage += (_, message) => messages.Add(message);
        manager.Enqueue(CreateDrawing("one.dwg"));
        manager.Enqueue(CreateDrawing("two.dwg"));
        Assert.Single(messages.Where(m => m.StartsWith("[保存警告]")));
        store.LastSaveFailed = false;
        manager.Enqueue(CreateDrawing("three.dwg"));
        Assert.Single(messages.Where(m => m.StartsWith("[保存恢复]")));
        Assert.Equal(3, manager.Tasks.Count);
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

    /// <summary>旧替身主动改实体状态；真实 TranslationService 返回结果而不改实体，另用真实服务回归覆盖。</summary>
    private sealed class FakeTranslator : ITranslationService
    {
        public int Calls { get; private set; }
        public TranslationBillingContext.Value? BillingContext { get; private set; }

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
            BillingContext = TranslationBillingContext.Current;
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
        public List<TextEntity> Entities { get; } = new();

        public CadWriteResult WriteTranslations(
            string sourceFilePath, string outputFilePath, List<TextEntity> entities,
            bool targetIsCjk = true, CancellationToken cancellationToken = default, WritebackOptions? options = null)
        {
            Written.Add(outputFilePath);
            // Snapshot the arguments at the writer boundary. TaskManager subsequently marks
            // successful originals WritebackSuccess; that must not rewrite this observation.
            Entities.AddRange(entities.Select(e => new TextEntity
            {
                Handle = e.Handle, SourceFilePath = e.SourceFilePath, PlainText = e.PlainText,
                RawText = e.RawText, TranslatedText = e.TranslatedText, Status = e.Status,
                GlossaryHit = e.GlossaryHit
            }));
            File.WriteAllText(outputFilePath, "written");
            return new CadWriteResult { SuccessCount = entities.Count, FailCount = 0 };
        }
    }

    [Fact]
    public void AccountStoreSwitchFlushesPreviousTasksAndNeverMergesOwners()
    {
        var a = new InMemoryTaskStore(); var b = new InMemoryTaskStore();
        using var manager = CreateManager(new FakeReader(), new FakeTranslator(), new FakeWriter(), a, Config(), out _);
        var first = manager.Enqueue(CreateDrawing("alice.dwg"));
        manager.SwitchAccountStore(b);
        Assert.Empty(manager.Tasks); Assert.Single(a.Saved);
        var second = manager.Enqueue(CreateDrawing("bob.dwg"));
        manager.SwitchAccountStore(a);
        Assert.Equal(first.Id, Assert.Single(manager.Tasks).Id);
        Assert.Equal(second.Id, Assert.Single(b.Saved).Id);
        manager.SwitchAccountStore(b);
        Assert.Equal(second.Id, Assert.Single(manager.Tasks).Id);
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
    public async Task Pipeline_RealTranslationService_MapsReturnedPairsWithoutWriteback()
    {
        var source=CreateDrawing("real-service.dwg"); var client=new FixedReplyClient();
        var translator=new TranslationService(new GlossaryService(),new FormatCodeParser(),client,"Translate to English.",maxRetryCount:0,consistencyService:new TranslationConsistencyService(""),maxConcurrency:1);
        var writer=new FakeWriter(); using var manager=CreateManager(new FakeReader(),translator,writer,new InMemoryTaskStore(),Config(),out var completed);
        manager.ConfigureRun("ZH","EN"); var task=manager.Enqueue(source); await manager.RunAsync();
        Assert.Equal(2,client.Calls); Assert.Equal(2,completed.Count); Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status);
        Assert.Equal(2,task.SuccessfulTranslations.Count); Assert.All(task.SuccessfulTranslations,p=>Assert.Equal("Valve feedback",p.TranslatedText));
        Assert.Empty(writer.Written); Assert.Null(task.OutputPath);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("handle")]
    [InlineData("text")]
    [InlineData("duplicate")]
    public async Task ReturnedPairs_RejectWrongIdentityBeforeAnyWrite(string mismatch)
    {
        var writer = new FakeWriter();
        var translator = new ReturnedPairTranslator(pairs =>
        {
            if (mismatch == "source") pairs[0].SourceFilePath = Path.Combine(_root, "other.dwg");
            if (mismatch == "handle") pairs[0].Handle = "NOT-IN-DRAWING";
            if (mismatch == "text") pairs[0].SourceText = "different source text";
            if (mismatch == "duplicate") pairs.Add(pairs[0]);
            return pairs;
        });
        using var manager = CreateManager(new FakeReader(), translator, writer,
            new InMemoryTaskStore(), Config(), out _);
        manager.ConfigureRun("ZH", "EN");
        var source = CreateDrawing("identity.dwg");
        var original = File.ReadAllBytes(source);
        var task = manager.Enqueue(source);
        await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.Failed, task.Status);
        Assert.Contains("不匹配或重复", task.Error);
        Assert.Empty(writer.Written);
        Assert.Null(task.OutputPath);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(TranslationStatus.TranslationFailed)]
    [InlineData(TranslationStatus.Skipped)]
    public async Task ReturnedPairs_PreserveFormattedTranslationAndExcludeUnwritableStatus(TranslationStatus excluded)
    {
        const string formatted=@"{\C1;Surface Roughness}"; var writer=new FakeWriter();
        var translator=new ReturnedPairTranslator(pairs=>{pairs[0].TranslatedText=formatted;pairs[0].GlossaryHit=true;pairs[1].Status=excluded;pairs[1].TranslatedText=excluded==TranslationStatus.Skipped?pairs[1].SourceText:"";return pairs;});
        using var manager=CreateManager(new FakeReader(),translator,writer,new InMemoryTaskStore(),Config(),out _); manager.ConfigureRun("ZH","EN");
        var task=manager.Enqueue(CreateDrawing("formatted-result.dwg")); await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status);
        var received=Assert.Single(task.SuccessfulTranslations.Where(p=>p.Handle=="A1")); Assert.Equal(formatted,received.TranslatedText); Assert.True(received.GlossaryHit);
        Assert.Equal(excluded==TranslationStatus.TranslationFailed?1:0,task.FailedCount); Assert.Empty(writer.Written); Assert.Null(task.OutputPath);
    }

    // Return-only contract: mutations performed by the old fake stay on copies, never originals.
    private sealed class ReturnedPairTranslator(Func<List<TranslationPair>, List<TranslationPair>> transform)
        : ITranslationService
    {
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) => Task.FromResult("Translated Text");
        public Task<List<TranslationPair>> TranslateBatchAsync(List<TextEntity> entities,
            string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
            => TranslateBatchWithProgressAsync(entities, sourceLanguage, targetLanguage, null, cancellationToken);
        public async Task<List<TranslationPair>> TranslateBatchWithProgressAsync(List<TextEntity> entities,
            string sourceLanguage, string targetLanguage, IProgress<TranslationPair>? progress,
            CancellationToken cancellationToken = default)
        {
            var copies = entities.Select(e => new TextEntity { Handle=e.Handle,
                SourceFilePath=e.SourceFilePath, PlainText=e.PlainText, RawText=e.RawText }).ToList();
            var pairs = transform(await new FakeTranslator().TranslateBatchAsync(copies,
                sourceLanguage, targetLanguage, cancellationToken));
            foreach (var pair in pairs) progress?.Report(pair);
            return pairs;
        }
    }
    private sealed class FixedReplyClient : IDeepSeekClient
    {
        public int Calls { get; private set; }
        public Task<string> ChatCompletionAsync(string systemPrompt, string userMessage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult("Valve feedback");
        }
    }

    [Fact]
    public async Task Pipeline_StopsAtReviewWithoutOutputAndReportsProgress()
    {
        var source=CreateDrawing("motor.dwg"); var writer=new FakeWriter(); var translator=new FakeTranslator();
        using var manager=CreateManager(new FakeReader(),translator,writer,new InMemoryTaskStore(),Config(),out var completed);
        var messages=new List<string>(); manager.ProgressMessage+=(_,m)=>messages.Add(m); var task=manager.Enqueue(source); await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status); Assert.Equal(2,completed.Count); Assert.Equal(100,task.Progress,1);
        Assert.Equal("online",task.BillingMode); Assert.Equal(task.Id,translator.BillingContext?.TaskId); Assert.Equal("online",translator.BillingContext?.Mode);
        Assert.Null(task.OutputPath); Assert.Empty(writer.Written); Assert.False(File.Exists(Path.Combine(_exportDir,"motor_en.dwg")));
        Assert.Contains(messages,m=>m.Contains("可以导出（校对可选）"));
    }

    [Fact]
    public async Task Pipeline_OneBadDrawing_DoesNotStopTheOthers()
    {
        var store=new InMemoryTaskStore(); var writer=new FakeWriter(); var badPath=CreateDrawing("bad.dwg");
        using var manager=CreateManager(new RoutedReader(badPath),new FakeTranslator(),writer,store,Config(),out _);
        manager.ConfigureRun("ZH","EN"); var bad=manager.Enqueue(badPath); var good=manager.Enqueue(CreateDrawing("good.dwg")); await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.Failed,bad.Status); Assert.Equal(TranslationTaskStatus.ReadyForReview,good.Status); Assert.Empty(writer.Written);
        Assert.Contains(store.Saved,t=>t.Id==good.Id&&t.Status==TranslationTaskStatus.ReadyForReview);
    }

    [Fact]
    public async Task Pipeline_ExistingOutput_DoesNotAffectTranslationOrOverwriteIt()
    {
        var existing=Path.Combine(_exportDir,"existing_en.dwg"); Directory.CreateDirectory(_exportDir); File.WriteAllText(existing,"keep"); var writer=new FakeWriter();
        using var manager=CreateManager(new FakeReader(),new FakeTranslator(),writer,new InMemoryTaskStore(),Config(),out _); manager.ConfigureRun("ZH","EN");
        var task=manager.Enqueue(CreateDrawing("existing.dwg")); await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status); Assert.Equal("keep",File.ReadAllText(existing)); Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task EmptyOutputConfigurationStillAllowsTranslationWithoutCreatingFiles()
    {
        var config=Config(); config.ExportDirectory=string.Empty; var writer=new FakeWriter(); using var manager=CreateManager(new FakeReader(),new FakeTranslator(),writer,new InMemoryTaskStore(),config,out _);
        manager.ConfigureRun("ZH","EN"); var task=manager.Enqueue(CreateDrawing("no-output-dir.dwg")); await manager.RunAsync();
        Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status); Assert.Empty(writer.Written); Assert.Null(task.OutputPath);
    }

    [Fact]
    public void ConfigureRun_UpdatesOnlyLanguagePair()
    {
        using var manager=CreateManager(new FakeReader(),new FakeTranslator(),new FakeWriter(),new InMemoryTaskStore(),Config(),out _); manager.ConfigureRun("EN","zh-tw");
        Assert.Equal("EN",manager.SourceLanguage); Assert.Equal("ZH-TW",manager.TargetLanguage);
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
    public async Task WorkerPipeline_RoutesHttpResultToReviewOrFailure(bool unauthorized)
    {
        using var handler=new WorkerHandler(unauthorized); using var http=new System.Net.Http.HttpClient(handler);
        var api=new DwgTranslator.Core.Api.WorkerApiClient(http,"https://worker.invalid",()=>"test-session","device","host"); var config=Config(); var writer=new FakeWriter();
        using var manager=CreateManager(new FakeReader(),new WorkerTranslationService(api,config,(t,r)=>t),writer,new InMemoryTaskStore(),config,out _); manager.ConfigureRun("ZH","EN");
        var task=manager.Enqueue(CreateDrawing("worker.dwg")); await manager.RunAsync();
        Assert.Equal(unauthorized?TranslationTaskStatus.Failed:TranslationTaskStatus.ReadyForReview,task.Status); Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task PartialRetryReusesCheckpointWithoutCreatingOutput()
    {
        using var handler=new PartialHandler(); using var http=new System.Net.Http.HttpClient(handler); var api=new DwgTranslator.Core.Api.WorkerApiClient(http,"https://worker.invalid",()=>"session","device","host");
        var config=Config(); var writer=new FakeWriter(); using var manager=CreateManager(new FakeReader(),new WorkerTranslationService(api,config,(t,r)=>t),writer,new InMemoryTaskStore(),config,out _);
        manager.ConfigureRun("ZH","EN"); var task=manager.Enqueue(CreateDrawing("partial.dwg")); await manager.RunAsync(); Assert.Single(task.SuccessfulTranslations);
        await manager.RetryTaskAsync(task.Id); Assert.Equal(new[]{2,1},handler.Counts); Assert.Equal(2,task.SuccessfulTranslations.Count);
        Assert.Equal(1, task.RetryCount);
        Assert.Equal(TranslationTaskStatus.ReadyForReview,task.Status); Assert.Empty(writer.Written); Assert.Null(task.OutputPath);
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


