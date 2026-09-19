using DwgTranslator.Core.Tasks;

namespace DwgTranslator.Core.Tests;

public sealed class TranslationTaskStateMachineTests
{
    [Fact]
    public void NewTaskStartsWithVersionedCreatedAudit()
    {
        var task = new TranslationTask("drawing.dwg");

        Assert.Equal(TranslationTask.CurrentSchemaVersion, task.SchemaVersion);
        Assert.Equal(TranslationTaskStatus.Pending, task.Status);
        Assert.Equal(TranslationTaskStatus.Pending, task.PreviousStatus);
        Assert.Equal(TranslationTaskTransitionReason.Created, task.LastTransitionReason);
        Assert.NotEqual(default, task.StatusChangedAt);
    }

    [Fact]
    public void ValidPipelineRecordsPreviousStateReasonAndTimestamp()
    {
        var task = new TranslationTask("drawing.dwg");
        var timestamp = task.CreatedAt.AddSeconds(1);

        task.TransitionTo(TranslationTaskStatus.Parsing, TranslationTaskTransitionReason.PipelineStarted, timestamp);
        task.TransitionTo(TranslationTaskStatus.Extracting, TranslationTaskTransitionReason.ExtractionStarted, timestamp.AddSeconds(1));
        task.TransitionTo(TranslationTaskStatus.Translating, TranslationTaskTransitionReason.TranslationStarted, timestamp.AddSeconds(2));
        task.TransitionTo(TranslationTaskStatus.ReadyForReview, TranslationTaskTransitionReason.AwaitingReview, timestamp.AddSeconds(3));
        task.TransitionTo(TranslationTaskStatus.Completed, TranslationTaskTransitionReason.ReviewCompleted, timestamp.AddSeconds(4));

        Assert.Equal(TranslationTaskStatus.Completed, task.Status);
        Assert.Equal(TranslationTaskStatus.ReadyForReview, task.PreviousStatus);
        Assert.Equal(TranslationTaskTransitionReason.ReviewCompleted, task.LastTransitionReason);
        Assert.Equal(timestamp.AddSeconds(4), task.StatusChangedAt);
        Assert.Equal(task.StatusChangedAt, task.UpdatedAt);
    }

    [Theory]
    [InlineData(TranslationTaskStatus.Pending, TranslationTaskStatus.Completed, TranslationTaskTransitionReason.ReviewCompleted)]
    [InlineData(TranslationTaskStatus.Pending, TranslationTaskStatus.Translating, TranslationTaskTransitionReason.TranslationStarted)]
    [InlineData(TranslationTaskStatus.ReadyForReview, TranslationTaskStatus.Pending, TranslationTaskTransitionReason.ResumedByUser)]
    [InlineData(TranslationTaskStatus.Completed, TranslationTaskStatus.Failed, TranslationTaskTransitionReason.Failed)]
    public void IllegalTransitionIsRejectedWithoutMutatingTask(
        TranslationTaskStatus current,
        TranslationTaskStatus target,
        TranslationTaskTransitionReason reason)
    {
        var task = new TranslationTask("drawing.dwg") { Status = current };
        var before = task.UpdatedAt;

        var error = Assert.Throws<InvalidOperationException>(() => task.TransitionTo(target, reason));

        Assert.Contains($"{current} -> {target}", error.Message, StringComparison.Ordinal);
        Assert.Equal(current, task.Status);
        Assert.Equal(before, task.UpdatedAt);
    }

    [Fact]
    public void PauseResumeRestoresExplicitStageOnlyWithResumeReason()
    {
        var task = new TranslationTask("drawing.dwg");
        task.TransitionTo(TranslationTaskStatus.Parsing, TranslationTaskTransitionReason.PipelineStarted);
        task.TransitionTo(TranslationTaskStatus.Paused, TranslationTaskTransitionReason.PausedByUser);

        Assert.False(task.CanTransitionTo(TranslationTaskStatus.Parsing, TranslationTaskTransitionReason.PipelineStarted));
        task.TransitionTo(TranslationTaskStatus.Parsing, TranslationTaskTransitionReason.ResumedByUser);

        Assert.Equal(TranslationTaskStatus.Parsing, task.Status);
        Assert.Equal(TranslationTaskStatus.Paused, task.PreviousStatus);
        Assert.Equal(TranslationTaskTransitionReason.ResumedByUser, task.LastTransitionReason);
    }

    [Theory]
    [InlineData(TranslationTaskStatus.Completed)]
    [InlineData(TranslationTaskStatus.Failed)]
    [InlineData(TranslationTaskStatus.Cancelled)]
    [InlineData(TranslationTaskStatus.PartiallyCompleted)]
    [InlineData(TranslationTaskStatus.Skipped)]
    public void RetryIsTheExplicitPathFromTerminalStateToPending(TranslationTaskStatus terminal)
    {
        var task = new TranslationTask("drawing.dwg") { Status = terminal };

        task.TransitionTo(TranslationTaskStatus.Pending, TranslationTaskTransitionReason.RetryRequested);

        Assert.Equal(TranslationTaskStatus.Pending, task.Status);
        Assert.Equal(terminal, task.PreviousStatus);
        Assert.Equal(TranslationTaskTransitionReason.RetryRequested, task.LastTransitionReason);
    }

    [Fact]
    public void LegacyJsonRowMigratesToCurrentSchemaAndPersistsReasonCode()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-task-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            File.WriteAllText(path, "[{\"Id\":\"legacy\",\"FilePath\":\"drawing.dwg\",\"Status\":7}]");
            var store = new JsonTaskStore(path);

            var task = Assert.Single(store.Load());

            Assert.Equal(TranslationTask.CurrentSchemaVersion, task.SchemaVersion);
            Assert.Equal(TranslationTaskStatus.Completed, task.Status);
            Assert.Equal(TranslationTaskTransitionReason.LegacyMigration, task.LastTransitionReason);
            Assert.NotEqual(default, task.StatusChangedAt);

            store.Save(new[] { task });
            var saved = File.ReadAllText(path);
            Assert.Contains("\"SchemaVersion\": 2", saved, StringComparison.Ordinal);
            Assert.Contains("\"LastTransitionReason\": \"LegacyMigration\"", saved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FutureSchemaRowIsIsolatedWithoutHidingHealthyRows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dwgc2e-task-future-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "tasks.json");
            var original = "[{\"SchemaVersion\":999,\"Id\":\"future\",\"FilePath\":\"future.dwg\"},{\"Id\":\"healthy\",\"FilePath\":\"healthy.dwg\",\"Status\":7}]";
            File.WriteAllText(path, original);
            var store = new JsonTaskStore(path);

            var task = Assert.Single(store.Load());
            Assert.Equal("healthy", task.Id);

            store.Save(new[] { task });
            Assert.NotNull(store.RecoveryFilePath);
            Assert.Equal(original, File.ReadAllText(store.RecoveryFilePath!));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
