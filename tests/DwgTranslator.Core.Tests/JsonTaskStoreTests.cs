using DwgTranslator.Core.Services;
using DwgTranslator.Core.Tasks;

namespace DwgTranslator.Core.Tests;

public class JsonTaskStoreTests
{
    [Fact]
    public void SaveKeepsTheReplacedQueueAsPreviousRollbackPoint()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-prev-" + Guid.NewGuid().ToString("N"), "tasks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("original.dwg") });
            Assert.Equal("original.dwg", Assert.Single(store.Load()).FilePath);

            // 逐步验证提交层：目标已存在时必须先落盘回滚点，且成功时清理掉。
            var probeTemp = Path.Combine(Path.GetDirectoryName(path)!, "probe-commit.tmp");
            File.WriteAllText(probeTemp, "next");
            var probeRollback = SafeFileCommit.Commit(probeTemp, path, overwrite: true);
            Assert.True(probeRollback == null,
                "probe rollback=" + (probeRollback ?? "cleaned") + " " + Describe(path)
                + " existsAfter=" + File.Exists(path + SafeFileCommit.RollbackSuffix));

            store.Save(new[] { new TranslationTask("new.dwg") });

            // 第二次保存仍必须原子完成：队列内容正确、没有临时文件残留、也不留下多余的回滚副本。
            Assert.False(store.LastSaveFailed, "after-second: " + Describe(path));
            Assert.Equal("new.dwg", Assert.Single(store.Load()).FilePath);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            Assert.False(File.Exists(path + SafeFileCommit.RollbackSuffix), "after-second: " + Describe(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static string Describe(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return "dir=" + string.Join(",", Directory.GetFiles(directory).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length));
    }

    [Fact]
    public void LockedDestinationPreservesPreviousStateAndNextSaveRecovers()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-store-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("original.dwg") });
            Assert.False(store.LastSaveFailed);
            var original = File.ReadAllBytes(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store.Save(new[] { new TranslationTask("new.dwg") });
                Assert.True(store.LastSaveFailed);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
            }
            store.Save(new[] { new TranslationTask("new.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.Equal("new.dwg", Assert.Single(store.Load()).FilePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidParentReportsFailureWithoutInterruptingCaller()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-store-parent-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "not a directory");
        try
        {
            var store = new JsonTaskStore(Path.Combine(path, "tasks.json"));
            store.Save(new[] { new TranslationTask("drawing.dwg") });
            Assert.True(store.LastSaveFailed);
            Assert.Equal("not a directory", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[null,{\"FilePath\":\"\"}]")]
    public void UnreadableStateIsPreservedBeforeNextSave(string content)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, content);
            var original = File.ReadAllBytes(path);
            var store = new JsonTaskStore(path);
            Assert.Empty(store.Load());
            store.Save(new[] { new TranslationTask("new.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.NotNull(store.RecoveryFilePath);
            Assert.Equal(original, File.ReadAllBytes(store.RecoveryFilePath!));
            Assert.Equal("new.dwg", Assert.Single(store.Load()).FilePath);
            store.Save(new[] { new TranslationTask("next.dwg") });
            Assert.Single(Directory.GetFiles(folder, "*.recovery-*.json"));
            Assert.Equal(original, File.ReadAllBytes(store.RecoveryFilePath!));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ExplicitClearAlsoPreservesUnreadableEvidence()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, "{broken");
            var store = new JsonTaskStore(path);
            Assert.Empty(store.Load());
            store.Clear();
            Assert.False(File.Exists(path));
            Assert.Equal("{broken", File.ReadAllText(store.RecoveryFilePath!));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(TranslationTaskStatus.Pending, false)]
    [InlineData(TranslationTaskStatus.Parsing, false)]
    [InlineData(TranslationTaskStatus.Extracting, false)]
    [InlineData(TranslationTaskStatus.Translating, false)]
    [InlineData(TranslationTaskStatus.LayoutOptimizing, false)]
    [InlineData(TranslationTaskStatus.Writing, false)]
    [InlineData(TranslationTaskStatus.Failed, false)]
    [InlineData(TranslationTaskStatus.Paused, false)]
    [InlineData(TranslationTaskStatus.ReadyForReview, true)]
    [InlineData(TranslationTaskStatus.Completed, true)]
    [InlineData(TranslationTaskStatus.Cancelled, true)]
    [InlineData(TranslationTaskStatus.PartiallyCompleted, true)]
    [InlineData(TranslationTaskStatus.Skipped, true)]
    public void EveryStageRestoresWithoutAutomaticallyRunningOrLosingCheckpoint(TranslationTaskStatus status, bool terminal)
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-stages-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var task = new TranslationTask("drawing.dwg") { Status = status, Progress = 42,
                CheckpointSignature = "checkpoint", OutputPath = "previous.dwg",
                StartedAt = DateTime.UtcNow.AddMinutes(-1), CompletedAt = DateTime.UtcNow, Error = "interrupted" };
            task.SuccessfulTranslations.Add(new() { Handle = "AB", TranslatedText = "retained" });
            var store = new JsonTaskStore(path);
            store.Save(new[] { task });
            var restored = Assert.Single(store.Load());
            Assert.Equal(terminal ? status : TranslationTaskStatus.Pending, restored.Status);
            Assert.Equal("checkpoint", restored.CheckpointSignature);
            Assert.Equal("retained", Assert.Single(restored.SuccessfulTranslations).TranslatedText);
            Assert.Equal("previous.dwg", restored.OutputPath);
            Assert.Equal(task.Id, restored.Id);
            Assert.Equal(terminal ? 42 : 0, restored.Progress);
            if (!terminal) { Assert.Null(restored.StartedAt); Assert.Null(restored.CompletedAt); Assert.Null(restored.Error); }
            Assert.Null(store.RecoveryFilePath);
        }
        finally { File.Delete(path); }
    }    [Fact]
    public void FailedRecoveryCopyBlocksReplacementAndCanRetry()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, "{broken");
            var store = new JsonTaskStore(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Empty(store.Load());
                store.Save(new[] { new TranslationTask("new.dwg") });
                Assert.True(store.LastSaveFailed);
                Assert.Null(store.RecoveryFilePath);
            }
            Assert.Equal("{broken", File.ReadAllText(path));
            store.Save(new[] { new TranslationTask("new.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.Equal("{broken", File.ReadAllText(store.RecoveryFilePath!));
            Assert.Equal("new.dwg", Assert.Single(store.Load()).FilePath);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ValidEmptyQueueNeverCreatesRecoverySnapshots()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, "[]");
            var store = new JsonTaskStore(path);
            Assert.Empty(store.Load());
            store.Save(new[] { new TranslationTask("new.dwg") });
            store.Save(Array.Empty<TranslationTask>());
            Assert.Null(store.RecoveryFilePath);
            Assert.Empty(Directory.GetFiles(folder, "*.recovery-*.json"));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public void RemovedUnreadableFileDoesNotMarkFutureHealthyStateForBackup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, "{broken");
            var store = new JsonTaskStore(path);
            Assert.Empty(store.Load());
            File.Delete(path);
            store.Save(new[] { new TranslationTask("first.dwg") });
            store.Save(new[] { new TranslationTask("second.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.Equal("second.dwg", Assert.Single(store.Load()).FilePath);
            Assert.Null(store.RecoveryFilePath);
            Assert.Empty(Directory.GetFiles(folder, "*.recovery-*.json"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void InterruptedTemporaryWriteNeverOverridesLastCommittedQueue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-interrupted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("committed.dwg") });
            var abandoned = path + ".abandoned.tmp";
            File.WriteAllText(abandoned, "[{\"FilePath\":\"partial");
            store = new JsonTaskStore(path);
            Assert.Equal("committed.dwg", Assert.Single(store.Load()).FilePath);
            store.Save(new[] { new TranslationTask("next.dwg") });
            Assert.False(store.LastSaveFailed);
            Assert.Equal("next.dwg", Assert.Single(new JsonTaskStore(path).Load()).FilePath);
            Assert.Equal("[{\"FilePath\":\"partial", File.ReadAllText(abandoned));
            Assert.Null(store.RecoveryFilePath);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FailedTaskEnumerationPreservesCommittedStateAndReportsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "dwgc2e-enumeration-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonTaskStore(path);
            store.Save(new[] { new TranslationTask("committed.dwg") });
            var original = File.ReadAllBytes(path);
            var error = Record.Exception(() => store.Save(BrokenSequence()));
            Assert.Null(error);
            Assert.True(store.LastSaveFailed);
            Assert.Equal(original, File.ReadAllBytes(path));
            store.Save(new[] { new TranslationTask("retry.dwg") });
            Assert.False(store.LastSaveFailed);
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<TranslationTask> BrokenSequence()
    {
        yield return new TranslationTask("partial.dwg");
        throw new IOException("Interrupted task snapshot");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Handle\":\"AB\",\"TranslatedText\":null}]")]
    [InlineData("[{\"Handle\":\"\"}]")]
    [InlineData("[{\"Handle\":\"AB\",\"Status\":999}]")]
    [InlineData("[{\"Handle\":\"AB\"},{\"Handle\":\"AB\"}]")]
    public void InvalidCheckpointIsNotUsedAndOriginalEvidenceIsPreserved(string checkpoint)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var json = "[{\"FilePath\":\"drawing.dwg\",\"CheckpointSignature\":\"old-signature\",\"SuccessfulTranslations\":" + checkpoint + "}]";
            File.WriteAllText(path, json);
            var store = new JsonTaskStore(path);
            var restored = Assert.Single(store.Load());
            Assert.NotNull(restored.SuccessfulTranslations);
            Assert.Empty(restored.SuccessfulTranslations);
            Assert.Equal(string.Empty, restored.CheckpointSignature);
            Assert.Equal(TranslationTaskStatus.Pending, restored.Status);
            store.Save(new[] { restored });
            Assert.False(store.LastSaveFailed);
            Assert.Equal(json, File.ReadAllText(store.RecoveryFilePath!));
            store.Save(new[] { restored });
            Assert.Single(Directory.GetFiles(folder, "*.recovery-*.json"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void UnknownTaskStateIsNotSilentlyRescheduledAndHealthyRowsSurvive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var json = "[{\"FilePath\":\"unknown.dwg\",\"Status\":999},{\"FilePath\":\"healthy.dwg\"}]";
            File.WriteAllText(path, json);
            var store = new JsonTaskStore(path);
            var rows = store.Load();
            Assert.Equal("healthy.dwg", Assert.Single(rows).FilePath);
            store.Save(rows);
            Assert.Equal(json, File.ReadAllText(store.RecoveryFilePath!));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void DuplicateTaskIdsCannotRestoreAmbiguousQueueRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var json = "[{\"Id\":\"same\",\"FilePath\":\"first.dwg\"},{\"Id\":\"same\",\"FilePath\":\"second.dwg\"}]";
            File.WriteAllText(path, json);
            var store = new JsonTaskStore(path);
            var rows = store.Load();
            Assert.Equal("first.dwg", Assert.Single(rows).FilePath);
            store.Save(rows);
            Assert.Equal(json, File.ReadAllText(store.RecoveryFilePath!));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("{\"FilePath\":\"bad.dwg\",\"Status\":\"unknown\"}")]
    [InlineData("{\"FilePath\":\"bad.dwg\",\"SuccessfulTranslations\":{}}")]
    [InlineData("{\"FilePath\":\"bad.dwg\",\"CreatedAt\":\"invalid-date\"}")]
    public void MalformedRowDoesNotHideOtherRecoverableTasks(string badRow)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var json = "[" + badRow + ",{\"FilePath\":\"healthy.dwg\"}]";
            File.WriteAllText(path, json);
            var store = new JsonTaskStore(path);
            var rows = store.Load();
            Assert.Equal("healthy.dwg", Assert.Single(rows).FilePath);
            store.Save(rows);
            Assert.Equal(json, File.ReadAllText(store.RecoveryFilePath!));
        }
        finally { Directory.Delete(folder, true); }
    }
}
