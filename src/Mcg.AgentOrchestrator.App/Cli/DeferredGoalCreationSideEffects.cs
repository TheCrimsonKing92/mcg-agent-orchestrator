using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>
/// Holds goal-creation lifecycle events until the optimistic state commit succeeds. Once committed,
/// later events (for example from <c>goal --run</c>) flow directly to the durable writer.
/// </summary>
internal sealed class DeferredGoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    private readonly object _gate = new();
    private readonly List<Action<IGoalLifecycleEventWriter>> _pending = [];
    private IGoalLifecycleEventWriter? _committedWriter;

    public void CommitTo(IGoalLifecycleEventWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Action<IGoalLifecycleEventWriter>[] pending;
        lock (_gate)
        {
            if (_committedWriter is not null)
                throw new InvalidOperationException("Goal-creation lifecycle effects were already committed.");

            _committedWriter = writer;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var append in pending)
            append(writer);
    }

    private void Append(Action<IGoalLifecycleEventWriter> append)
    {
        IGoalLifecycleEventWriter? writer;
        lock (_gate)
        {
            writer = _committedWriter;
            if (writer is null)
            {
                _pending.Add(append);
                return;
            }
        }

        append(writer);
    }

    public void AppendTimelineEvent(ProgressEvent value) => Append(writer => writer.AppendTimelineEvent(value));
    public void AppendGoalCreated(GoalId goalId, string objective) => Append(writer => writer.AppendGoalCreated(goalId, objective));
    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) => Append(writer => writer.AppendClarificationNeeded(goalId, clarificationId));
    public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> keys, string command) => Append(writer => writer.AppendStaleClarificationDetected(goalId, keys, command));
    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) => Append(writer => writer.AppendTaskDispatched(goalId, taskId, role, workerName));
    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) => Append(writer => writer.AppendWorkerProgress(goalId, stdoutBytes, stderrBytes, lastProgressAt));
    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) => Append(writer => writer.AppendAcceptanceResult(goalId, pass, failures));
    public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string hash) => Append(writer => writer.AppendAcceptanceCriterionWaived(goalId, criterion, actor, recordedAt, reason, hash));
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) => Append(writer => writer.AppendGoalLanded(goalId, integrationBranch, goalBranch));
    public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) => Append(writer => writer.AppendGoalLandedFromAncestry(goalId, goalBranch, branchTip, mainSha));
    public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) => Append(writer => writer.AppendGoalLandedFromMergeEvidence(goalId, goalBranch, integrateSha, mainSha));
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) => Append(writer => writer.AppendGoalEscalated(goalId, state, reason, source));
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) => Append(writer => writer.AppendGoalEscalated(goalId, state, status, reason, source));
    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) => Append(writer => writer.AppendGoalEvictedFromConductor(goalId, status, trigger));
    public void AppendCleanedUp(GoalId goalId) => Append(writer => writer.AppendCleanedUp(goalId));

    public void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile) =>
        Append(writer => writer.AppendProgressiveReviewGlanceReceipt(
            goalId, taskId, trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, wallTime, model, profile));

    public void AppendProgressiveReviewGlanceGuardReceipt(GoalId goalId, TaskId taskId, ProgressiveReviewGlanceGuardReceipt receipt) =>
        Append(writer => writer.AppendProgressiveReviewGlanceGuardReceipt(goalId, taskId, receipt));

    public void AppendProgressiveReviewGlanceSummary(
        GoalId goalId,
        int totalGlances,
        int onTrack,
        int concern,
        int fundamentalMisdirection,
        int invalid,
        int totalTokens) =>
        Append(writer => writer.AppendProgressiveReviewGlanceSummary(
            goalId, totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens));
}

/// <summary>
/// Defers clarification raises while goal state is only prepared in memory. Reads continue to use
/// the live store; a failed optimistic commit simply discards this command-scoped instance.
/// </summary>
internal sealed class DeferredGoalCreationCollaborationWriter(ICollaborationItemStore store)
{
    private readonly object _gate = new();
    private readonly List<Func<CancellationToken, Task>> _pending = [];
    private bool _committed;

    public Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_committed)
                return store.RaiseAsync(type, goalId, subject, body, correlationKey, cancellationToken);

            var pending = new CollaborationItem(
                Guid.NewGuid().ToString("n"),
                type,
                goalId,
                CollaborationItemStatus.Raised,
                subject,
                body,
                correlationKey,
                DateTimeOffset.UtcNow,
                null,
                null);
            _pending.Add(async token =>
                _ = await store.RaiseAsync(type, goalId, subject, body, correlationKey, token).ConfigureAwait(false));
            return Task.FromResult(pending);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>[] pending;
        lock (_gate)
        {
            if (_committed)
                throw new InvalidOperationException("Goal-creation collaboration effects were already committed.");

            _committed = true;
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var write in pending)
            await write(cancellationToken).ConfigureAwait(false);
    }
}
