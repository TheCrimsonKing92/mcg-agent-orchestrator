using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private const int MaxWorkerBuildCheckRecoveries = 2;
    private Func<GoalId, TaskId, string, TaskSpec>? _workerBuildRecoveryRetry;
    private Func<GoalId, string> _workerBuildArtifactsPath =
        goalId => DotnetBuildEnvironmentManager.GoalArtifactsPath(goalId);
    private Func<WorkerBuildCapacitySnapshot> _workerBuildCapacityReader = () => WorkerBuildCapacityGate.ReadHostSnapshot(
        DotnetBuildEnvironmentManager.CaptureStorageRoot(), TimeProvider.System, WorkerBuildCapacityGate.IsExecutionLockHeld);

    internal void UseWorkerBuildCapacityReader(Func<WorkerBuildCapacitySnapshot> reader) =>
        _workerBuildCapacityReader = reader;

    private ConductorAdvanceResult? TryRecoverFailedWorkerBuildCheck(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, GoalLifecycleState state,
        FailedGoalRecoveryDecision decision, IReadOnlyList<FailedGoalPendingNote> pendingNotes)
    {
        var task = goal.Tasks.FirstOrDefault(candidate =>
            (candidate.Id == decision.Identity.TaskId ||
             (decision.Identity.TaskId is null &&
              decision.DiscriminatingEvidence == "failed-terminal-fallthrough")) &&
            candidate.Status == WorkTaskStatus.Failed &&
            candidate.RequiredRole == AgentRole.Developer &&
            candidate.LastVerification is { } verification &&
            TaskOutcomeClassifier.TryExtractRule(
                DispatchFailureClassifier.Classify(candidate, verification).ClassifierReceipt) is { } rule &&
            (string.Equals(rule, DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed, StringComparison.Ordinal) ||
             (string.Equals(rule, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing,
                 StringComparison.Ordinal) && WorkerBuildCheckRecoveryText.NoBuildCause(
                     verification.StandardError + Environment.NewLine + verification.StandardOutput) != WorkerBuildNoBuildCause.None) ||
             (string.Equals(rule, DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing, StringComparison.Ordinal) &&
              WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(candidate))));
        if (task is null) return null;
        var capacity = WorkerBuildCapacityEvidence.LastDispatchEndedOnCapacity(task);
        var cause = capacity ? WorkerBuildNoBuildCause.SlotCapacity : WorkerBuildCheckRecoveryText.NoBuildCause(
            task.LastVerification!.StandardError + Environment.NewLine + task.LastVerification.StandardOutput);
        if (!capacity && cause == WorkerBuildNoBuildCause.SlotCapacity &&
            TaskOutcomeClassifier.TryExtractRule(DispatchFailureClassifier.Classify(task, task.LastVerification!).ClassifierReceipt) !=
                DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing)
            cause = WorkerBuildNoBuildCause.None;
        var errors = cause == WorkerBuildNoBuildCause.SlotCapacity ? null : WorkerBuildLogErrorReader.ReadNewest(_workerBuildArtifactsPath(goal.Id));
        var noBuild = errors is null && cause != WorkerBuildNoBuildCause.None;
        if (errors is null && !noBuild)
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            var escalated = Escalate(goal, goalPrefix, policy, state, decision.Reason);
            return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                Decision = FailedGoalRecoveryDecisionRecords.BuildLogUnavailable(decision.Identity, decision.Reason) } };
        }
        _beforeFailedGoalRecoveryEffect?.Invoke(goal, decision);
        if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out var staleReason))
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, staleReason) {
                    Decision = FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(decision.Identity, staleReason) });
        }
        ApplyPendingFailedGoalNotes(goal, pendingNotes);
        var capacityRounds = capacity ? WorkerBuildCapacityEvidence.CapacityDispatchCount(task) : 0;
        var capacityVerdict = WorkerBuildCapacityVerdict.Retry;
        if (capacity)
        {
            var snapshot = _workerBuildCapacityReader();
            capacityVerdict = WorkerBuildCapacityGate.Decide(snapshot, capacityRounds);
            if (capacityVerdict == WorkerBuildCapacityVerdict.Hold)
            {
                var reason = WorkerBuildCheckRecoveryText.CapacityHeldReason(snapshot, capacityRounds);
                return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(state, reason) {
                    Decision = FailedGoalRecoveryDecisionRecords.BuildCapacityHeld(decision.Identity, reason) });
            }
        }
        var errorBlock = WorkerBuildCheckRecoveryText.ErrorBlock(errors?.Format(20), cause);
        var worktreePath = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        if (worktreePath is null)
        {
            var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, "checkpoint commit failed: goal worktree unavailable.", errorBlock);
            var escalated = Escalate(goal, goalPrefix, policy, state, reason);
            return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                Decision = FailedGoalRecoveryDecisionRecords.WorktreeUnavailable(decision.Identity, reason) } };
        }
        var committer = new DispatchWorktreeCommitter();
        var dispatchedAt = task.LastDispatch?.DispatchedAt ?? DateTimeOffset.MinValue;
        if (!committer.TryInspectGoalWorktree(worktreePath, goal.Id, dispatchedAt, out var worktree, forceRefresh: true))
        {
            var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, "checkpoint commit failed: goal worktree inspection unavailable.", errorBlock);
            var escalated = Escalate(goal, goalPrefix, policy, state, reason);
            return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                Decision = FailedGoalRecoveryDecisionRecords.WorktreeInspectionUnavailable(decision.Identity, reason) } };
        }
        var exhausted = capacity ? capacityVerdict == WorkerBuildCapacityVerdict.Exhausted : task.WorkerBuildCheckRecoveryCount >= MaxWorkerBuildCheckRecoveries;
        var subject = capacity ? WorkerBuildCheckRecoveryText.CapacityCheckpointSubject(exhausted, capacityRounds, task.Id.Value) :
            WorkerBuildCheckRecoveryText.CheckpointSubject(exhausted, task.WorkerBuildCheckRecoveryCount, task.Id.Value);
        var checkpoint = worktree.Head;
        var committed = false;
        if (!worktree.IsClean)
        {
            var commit = committer.TryCommitWorktreeEdits(worktreePath, subject, worktree.DirtyPaths);
            if (!commit.Succeeded)
            {
                var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, $"checkpoint commit failed: {commit.Diagnostic}", errorBlock);
                var escalated = Escalate(goal, goalPrefix, policy, state, reason);
                return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                    Decision = FailedGoalRecoveryDecisionRecords.CheckpointCommitFailed(decision.Identity, reason) } };
            }
            if (!committer.TryInspectGoalWorktree(worktreePath, goal.Id, dispatchedAt, out var after, forceRefresh: true) || !after.IsClean)
            {
                var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, "checkpoint commit failed: worktree remained dirty after commit.", errorBlock);
                var escalated = Escalate(goal, goalPrefix, policy, state, reason);
                return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                    Decision = FailedGoalRecoveryDecisionRecords.CheckpointDirtyAfterCommit(decision.Identity, reason) } };
            }
            checkpoint = after.Head;
            committed = true;
        }

        if (exhausted || _workerBuildRecoveryRetry is null)
        {
            var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, $"checkpoint commit: {checkpoint}", errorBlock);
            var escalated = Escalate(goal, goalPrefix, policy, state, reason);
            return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                Decision = capacity ? FailedGoalRecoveryDecisionRecords.BuildCapacityRetriesExhausted(decision.Identity, reason) :
                    FailedGoalRecoveryDecisionRecords.BuildCheckRecoveryExhausted(decision.Identity, reason) } };
        }
        var feedback = capacity ? WorkerBuildCheckRecoveryText.CapacityRetryFeedback(capacityRounds, committed, checkpoint) :
            WorkerBuildCheckRecoveryText.RetryFeedback(task.WorkerBuildCheckRecoveryCount, committed, checkpoint, noBuild ? null : errors!.Format(50), cause);
        try { _workerBuildRecoveryRetry(goal.Id, task.Id, feedback); }
        catch (InvalidOperationException ex)
        {
            var reason = WorkerBuildCheckRecoveryText.EscalationReason(decision.Reason, $"automatic retry failed: {ex.Message}{Environment.NewLine}checkpoint commit: {checkpoint}", errorBlock);
            var escalated = Escalate(goal, goalPrefix, policy, state, reason);
            return escalated with { Outcome = ((ConductorAdvanceOutcome.Escalated)escalated.Outcome) with {
                Decision = FailedGoalRecoveryDecisionRecords.BuildCheckRetryFailed(decision.Identity, reason) } };
        }

        var refreshedGoal = GetCurrentGoal(goal);
        var refreshedTask = refreshedGoal.Tasks.Single(candidate => candidate.Id == task.Id);
        if (refreshedTask.Status is not (WorkTaskStatus.Assigned or WorkTaskStatus.Pending) ||
            refreshedGoal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }))
            return MakeResult(refreshedGoal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal)), WorkerBuildCheckRecoveryText.RetryAppliedHeld) {
                    Decision = FailedGoalRecoveryDecisionRecords.BuildCheckRetryApplied(decision.Identity, WorkerBuildCheckRecoveryText.RetryAppliedHeld) });
        return ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
    }
}
