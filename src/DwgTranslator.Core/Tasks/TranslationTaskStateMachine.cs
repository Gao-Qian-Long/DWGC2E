using System;
using System.Collections.Generic;

namespace DwgTranslator.Core.Tasks;

/// <summary>
/// Stable reason codes persisted with the task. These codes are intentionally independent from
/// localized UI text so support logs and future storage migrations can explain why a state changed.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum TranslationTaskTransitionReason
{
    Created = 0,
    PipelineStarted,
    ExtractionStarted,
    TranslationStarted,
    LayoutOptimizationStarted,
    WritingStarted,
    AwaitingReview,
    ReviewCompleted,
    CompletedWithoutContent,
    CompletedWithFailures,
    Failed,
    CancelledByUser,
    PausedByUser,
    ResumedByUser,
    RetryRequested,
    RecoveredAfterInterruption,
    Skipped,
    LegacyMigration
}

/// <summary>
/// The single authority for task status changes. UI code may inspect a task, but production task
/// execution must use <see cref="TransitionTo"/> instead of assigning Status directly.
/// </summary>
public static class TranslationTaskStateMachine
{
    private static readonly IReadOnlyDictionary<TranslationTaskStatus, HashSet<TranslationTaskStatus>> Allowed =
        new Dictionary<TranslationTaskStatus, HashSet<TranslationTaskStatus>>
        {
            [TranslationTaskStatus.Pending] = [TranslationTaskStatus.Parsing, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Skipped, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.Parsing] = [TranslationTaskStatus.Extracting, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.Extracting] = [TranslationTaskStatus.Translating, TranslationTaskStatus.Completed, TranslationTaskStatus.PartiallyCompleted, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.Translating] = [TranslationTaskStatus.LayoutOptimizing, TranslationTaskStatus.Writing, TranslationTaskStatus.ReadyForReview, TranslationTaskStatus.Completed, TranslationTaskStatus.PartiallyCompleted, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.LayoutOptimizing] = [TranslationTaskStatus.Writing, TranslationTaskStatus.ReadyForReview, TranslationTaskStatus.Completed, TranslationTaskStatus.PartiallyCompleted, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.Writing] = [TranslationTaskStatus.ReadyForReview, TranslationTaskStatus.Completed, TranslationTaskStatus.PartiallyCompleted, TranslationTaskStatus.Paused, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.ReadyForReview] = [TranslationTaskStatus.Completed],
            [TranslationTaskStatus.Completed] = [],
            [TranslationTaskStatus.Failed] = [],
            [TranslationTaskStatus.Cancelled] = [],
            [TranslationTaskStatus.Paused] = [TranslationTaskStatus.Pending, TranslationTaskStatus.Parsing, TranslationTaskStatus.Extracting, TranslationTaskStatus.Translating, TranslationTaskStatus.LayoutOptimizing, TranslationTaskStatus.Writing, TranslationTaskStatus.Cancelled, TranslationTaskStatus.Failed],
            [TranslationTaskStatus.PartiallyCompleted] = [],
            [TranslationTaskStatus.Skipped] = []
        };

    public static bool CanTransitionTo(this TranslationTask task, TranslationTaskStatus target, TranslationTaskTransitionReason reason)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!Enum.IsDefined(task.Status) || !Enum.IsDefined(target) || !Enum.IsDefined(reason)) return false;

        // Retry and crash recovery are explicit restart operations. They are the only legal path
        // from a terminal state back to Pending. Recovery may also normalize an already-pending row.
        if (reason is TranslationTaskTransitionReason.RetryRequested or TranslationTaskTransitionReason.RecoveredAfterInterruption)
            return target == TranslationTaskStatus.Pending;

        // Legacy data is validated separately by JsonTaskStore. This reason only records the
        // migration and must not be used by live business logic to bypass the matrix.
        if (reason == TranslationTaskTransitionReason.LegacyMigration)
            return true;

        if (target == task.Status) return false;
        if (!Allowed.TryGetValue(task.Status, out var targets) || !targets.Contains(target)) return false;

        // Paused is a guarded holding state: a pipeline-stage reason must not accidentally
        // resume work. Only an explicit user resume, cancellation, or failure may leave it.
        if (task.Status == TranslationTaskStatus.Paused)
        {
            return reason switch
            {
                TranslationTaskTransitionReason.ResumedByUser =>
                    target is TranslationTaskStatus.Pending
                        or TranslationTaskStatus.Parsing
                        or TranslationTaskStatus.Extracting
                        or TranslationTaskStatus.Translating
                        or TranslationTaskStatus.LayoutOptimizing
                        or TranslationTaskStatus.Writing,
                TranslationTaskTransitionReason.CancelledByUser => target == TranslationTaskStatus.Cancelled,
                TranslationTaskTransitionReason.Failed => target == TranslationTaskStatus.Failed,
                _ => false
            };
        }

        return reason switch
        {
            TranslationTaskTransitionReason.PipelineStarted => target == TranslationTaskStatus.Parsing,
            TranslationTaskTransitionReason.ExtractionStarted => target == TranslationTaskStatus.Extracting,
            TranslationTaskTransitionReason.TranslationStarted => target == TranslationTaskStatus.Translating,
            TranslationTaskTransitionReason.LayoutOptimizationStarted => target == TranslationTaskStatus.LayoutOptimizing,
            TranslationTaskTransitionReason.WritingStarted => target == TranslationTaskStatus.Writing,
            TranslationTaskTransitionReason.AwaitingReview => target == TranslationTaskStatus.ReadyForReview,
            TranslationTaskTransitionReason.ReviewCompleted => task.Status == TranslationTaskStatus.ReadyForReview && target == TranslationTaskStatus.Completed,
            TranslationTaskTransitionReason.CompletedWithoutContent => target == TranslationTaskStatus.Completed,
            TranslationTaskTransitionReason.CompletedWithFailures => target == TranslationTaskStatus.PartiallyCompleted,
            TranslationTaskTransitionReason.Failed => target == TranslationTaskStatus.Failed,
            TranslationTaskTransitionReason.CancelledByUser => target == TranslationTaskStatus.Cancelled,
            TranslationTaskTransitionReason.PausedByUser => target == TranslationTaskStatus.Paused,
            TranslationTaskTransitionReason.ResumedByUser => task.Status == TranslationTaskStatus.Paused && target != TranslationTaskStatus.Cancelled && target != TranslationTaskStatus.Failed,
            TranslationTaskTransitionReason.Skipped => target == TranslationTaskStatus.Skipped,
            TranslationTaskTransitionReason.Created => false,
            _ => false
        };
    }

    public static void TransitionTo(this TranslationTask task, TranslationTaskStatus target,
        TranslationTaskTransitionReason reason, DateTime? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.CanTransitionTo(target, reason))
            throw new InvalidOperationException($"Illegal task transition: {task.Status} -> {target} ({reason}).");

        var timestamp = occurredAt ?? DateTime.UtcNow;
        if (task.CreatedAt != default && timestamp < task.CreatedAt) timestamp = task.CreatedAt;
        if (task.UpdatedAt != default && timestamp < task.UpdatedAt) timestamp = task.UpdatedAt;

        task.PreviousStatus = task.Status;
        task.Status = target;
        task.LastTransitionReason = reason;
        task.StatusChangedAt = timestamp;
        task.UpdatedAt = timestamp;
        task.SchemaVersion = TranslationTask.CurrentSchemaVersion;
    }
}
