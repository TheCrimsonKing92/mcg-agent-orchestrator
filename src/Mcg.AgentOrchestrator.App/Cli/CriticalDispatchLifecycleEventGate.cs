using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>
/// Holds conductor dispatch projections until their critical snapshot checkpoint commits.
/// Other lifecycle events retain their existing emission points.
/// </summary>
internal sealed class CriticalDispatchLifecycleEventGate(IGoalLifecycleEventWriter inner) : IGoalLifecycleEventWriter
{
    private readonly object _sync = new();
    private readonly Dictionary<GoalId, DispatchHold> _holds = [];

    internal IDisposable Hold(GoalId goalId, AgentOrchestratorKernel kernel)
    {
        lock (_sync)
        {
            var hold = new DispatchHold(this, goalId, kernel);
            _holds.Add(goalId, hold);
            return hold;
        }
    }

    internal void CommitCheckpoint(GoalId goalId, Action persist)
    {
        try
        {
            persist();
        }
        catch
        {
            Discard(goalId);
            throw;
        }
        Release(goalId);
    }

    internal void Release(GoalId goalId)
    {
        lock (_sync)
        {
            if (_holds.TryGetValue(goalId, out var hold))
            {
                var committed = hold.Pending.ToArray();
                hold.Pending.Clear();
                foreach (var entry in committed)
                    inner.AppendTimelineEvent(entry);
            }
        }
    }

    internal void Discard(GoalId goalId)
    {
        lock (_sync)
        {
            if (_holds.TryGetValue(goalId, out var hold))
                hold.Pending.Clear();
        }
    }

    private void EndHold(DispatchHold hold)
    {
        lock (_sync)
        {
            if (!_holds.Remove(hold.GoalId))
                return;

            // Preparation can stop without reaching a checkpoint. Preserve its existing
            // projection only when it survived rollback or an authoritative rebase.
            var goal = hold.Kernel.Goals.FirstOrDefault(goal => goal.Id == hold.GoalId);
            foreach (var entry in hold.Pending)
            {
                if (goal?.Timeline.Any(current =>
                    current.Kind == entry.Kind && current.OccurredAt == entry.OccurredAt &&
                    current.TaskId == entry.TaskId && current.Message == entry.Message) == true)
                    inner.AppendTimelineEvent(entry);
            }
        }
    }

    public void AppendTimelineEvent(ProgressEvent progressEvent)
    {
        lock (_sync)
        {
            if (progressEvent.Kind == ProgressKind.TaskDispatchRecorded &&
                _holds.TryGetValue(progressEvent.GoalId, out var hold))
            {
                hold.Pending.Add(progressEvent);
                return;
            }
            inner.AppendTimelineEvent(progressEvent);
        }
    }

    public void AppendGoalCreated(GoalId goalId, string objective) => inner.AppendGoalCreated(goalId, objective);
    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) => inner.AppendClarificationNeeded(goalId, clarificationId);
    public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> keys, string command) => inner.AppendStaleClarificationDetected(goalId, keys, command);
    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) => inner.AppendTaskDispatched(goalId, taskId, role, workerName);
    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) => inner.AppendWorkerProgress(goalId, stdoutBytes, stderrBytes, lastProgressAt);
    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) => inner.AppendAcceptanceResult(goalId, pass, failures);
    public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string hash) => inner.AppendAcceptanceCriterionWaived(goalId, criterion, actor, recordedAt, reason, hash);
    public void AppendCriteriaCorrectionIgnored(GoalId goalId, TaskId? taskId, string source) => inner.AppendCriteriaCorrectionIgnored(goalId, taskId, source);
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) => inner.AppendGoalLanded(goalId, integrationBranch, goalBranch);
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch, LandingAdmissionReceipt receipt) => inner.AppendGoalLanded(goalId, integrationBranch, goalBranch, receipt);
    public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) => inner.AppendGoalLandedFromAncestry(goalId, goalBranch, branchTip, mainSha);
    public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) => inner.AppendGoalLandedFromMergeEvidence(goalId, goalBranch, integrateSha, mainSha);
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) => inner.AppendGoalEscalated(goalId, state, reason, source);
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) => inner.AppendGoalEscalated(goalId, state, status, reason, source);
    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) => inner.AppendGoalEvictedFromConductor(goalId, status, trigger);
    public void AppendCleanedUp(GoalId goalId) => inner.AppendCleanedUp(goalId);
    public void AppendProgressiveReviewGlanceReceipt(GoalId goalId, TaskId taskId, string trigger, string inputsHash, string verdict, string note, int inputTokens, int outputTokens, int totalTokens, TimeSpan wallTime, string? model, string? profile) => inner.AppendProgressiveReviewGlanceReceipt(goalId, taskId, trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, wallTime, model, profile);
    public void AppendProgressiveReviewGlanceGuardReceipt(GoalId goalId, TaskId taskId, ProgressiveReviewGlanceGuardReceipt receipt) => inner.AppendProgressiveReviewGlanceGuardReceipt(goalId, taskId, receipt);
    public void AppendProgressiveReviewGlanceCircuitReceipt(GoalId goalId, TaskId taskId, ProgressiveReviewGlanceCircuitReceipt receipt) => inner.AppendProgressiveReviewGlanceCircuitReceipt(goalId, taskId, receipt);
    public void AppendProgressiveReviewGlanceSummary(GoalId goalId, int totalGlances, int onTrack, int concern, int fundamentalMisdirection, int invalid, int totalTokens) => inner.AppendProgressiveReviewGlanceSummary(goalId, totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens);

    private sealed class DispatchHold(CriticalDispatchLifecycleEventGate owner, GoalId goalId, AgentOrchestratorKernel kernel) : IDisposable
    {
        internal GoalId GoalId { get; } = goalId;
        internal AgentOrchestratorKernel Kernel { get; } = kernel;
        internal List<ProgressEvent> Pending { get; } = [];
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndHold(this);
        }
    }
}
