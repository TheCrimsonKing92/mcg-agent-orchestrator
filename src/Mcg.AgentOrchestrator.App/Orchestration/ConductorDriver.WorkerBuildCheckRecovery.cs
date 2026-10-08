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
            string.Equals(
                TaskOutcomeClassifier.TryExtractRule(
                    DispatchFailureClassifier.Classify(candidate, verification).ClassifierReceipt),
                DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed,
                StringComparison.Ordinal));
        if (task is null) return null;
        var errors = WorkerBuildLogErrorReader.ReadNewest(_workerBuildArtifactsPath(goal.Id));
        var lockTimeout = errors is null && IsBuildSlotLockTimeout(task.LastVerification!);
        if (errors is null && !lockTimeout)
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
        var errorBlock = WorkerBuildCheckRecoveryText.ErrorBlock(errors?.Format(20));
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
        var exhausted = task.WorkerBuildCheckRecoveryCount >= MaxWorkerBuildCheckRecoveries;
        var subject = WorkerBuildCheckRecoveryText.CheckpointSubject(exhausted, task.WorkerBuildCheckRecoveryCount, task.Id.Value);
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
                Decision = FailedGoalRecoveryDecisionRecords.BuildCheckRecoveryExhausted(decision.Identity, reason) } };
        }
        var feedback = WorkerBuildCheckRecoveryText.RetryFeedback(task.WorkerBuildCheckRecoveryCount, committed, checkpoint, lockTimeout ? null : errors!.Format(50));
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

    private static bool IsBuildSlotLockTimeout(TaskVerificationRecord verification)
    {
        var output = verification.StandardError + Environment.NewLine + verification.StandardOutput;
        return output.Contains(WorkerBuildCheckRecoveryText.BuildSlotLockTimeout, StringComparison.Ordinal) &&
            !output.Split('\n').Any(WorkerBuildLogErrorReader.IsCompilerDiagnostic);
    }
}
