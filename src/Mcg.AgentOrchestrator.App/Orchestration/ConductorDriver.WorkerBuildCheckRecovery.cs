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
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state,
        FailedGoalRecoveryDecision decision,
        IReadOnlyList<FailedGoalPendingNote> pendingNotes)
    {
        var task = goal.Tasks.FirstOrDefault(candidate =>
            candidate.Id == decision.Identity.TaskId &&
            candidate.Status == WorkTaskStatus.Failed &&
            candidate.RequiredRole == AgentRole.Developer &&
            candidate.LastVerification is { } verification &&
            string.Equals(
                TaskOutcomeClassifier.TryExtractRule(
                    DispatchFailureClassifier.Classify(candidate, verification).ClassifierReceipt),
                DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed,
                StringComparison.Ordinal));
        if (task is null)
            return null;

        var errors = WorkerBuildLogErrorReader.ReadNewest(_workerBuildArtifactsPath(goal.Id));
        if (errors is null)
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return Escalate(goal, goalPrefix, policy, state, decision.Reason);
        }

        _beforeFailedGoalRecoveryEffect?.Invoke(goal, decision);
        if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out var staleReason))
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, staleReason));
        }
        ApplyPendingFailedGoalNotes(goal, pendingNotes);

        var errorBlock = errors.Format(20);
        var worktreePath = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        if (worktreePath is null)
            return Escalate(goal, goalPrefix, policy, state,
                $"{decision.Reason}{Environment.NewLine}checkpoint commit failed: goal worktree unavailable.{Environment.NewLine}{errorBlock}");

        var committer = new DispatchWorktreeCommitter();
        var dispatchedAt = task.LastDispatch?.DispatchedAt ?? DateTimeOffset.MinValue;
        if (!committer.TryInspectGoalWorktree(worktreePath, goal.Id, dispatchedAt, out var worktree, forceRefresh: true))
            return Escalate(goal, goalPrefix, policy, state,
                $"{decision.Reason}{Environment.NewLine}checkpoint commit failed: goal worktree inspection unavailable.{Environment.NewLine}{errorBlock}");

        var exhausted = task.WorkerBuildCheckRecoveryCount >= MaxWorkerBuildCheckRecoveries;
        var subject = exhausted
            ? $"checkpoint: worker-build-check-failed exhausted 2/2 for task {task.Id.Value}"
            : $"checkpoint: worker-build-check-failed recovery {task.WorkerBuildCheckRecoveryCount + 1}/2 for task {task.Id.Value}";
        var checkpoint = worktree.Head;
        var committed = false;
        if (!worktree.IsClean)
        {
            var commit = committer.TryCommitWorktreeEdits(worktreePath, subject, worktree.DirtyPaths);
            if (!commit.Succeeded)
                return Escalate(goal, goalPrefix, policy, state,
                    $"{decision.Reason}{Environment.NewLine}checkpoint commit failed: {commit.Diagnostic}{Environment.NewLine}{errorBlock}");
            if (!committer.TryInspectGoalWorktree(worktreePath, goal.Id, dispatchedAt, out var after, forceRefresh: true) || !after.IsClean)
                return Escalate(goal, goalPrefix, policy, state,
                    $"{decision.Reason}{Environment.NewLine}checkpoint commit failed: worktree remained dirty after commit.{Environment.NewLine}{errorBlock}");
            checkpoint = after.Head;
            committed = true;
        }

        if (exhausted || _workerBuildRecoveryRetry is null)
            return Escalate(goal, goalPrefix, policy, state,
                $"{decision.Reason}{Environment.NewLine}checkpoint commit: {checkpoint}{Environment.NewLine}{errorBlock}");

        var feedback = $"worker-build-check-failed automatic recovery {task.WorkerBuildCheckRecoveryCount + 1}/2:" +
            Environment.NewLine + (committed ? $"checkpoint commit {checkpoint}" :
                $"no uncommitted changes were found; goal branch HEAD {checkpoint}") +
            Environment.NewLine + "Fix only what the build reports below, and run scripts/Invoke-WorkerBuildCheck.ps1 after the last edit." +
            Environment.NewLine + errors.Format(50);
        try
        {
            _workerBuildRecoveryRetry(goal.Id, task.Id, feedback);
        }
        catch (InvalidOperationException ex)
        {
            return Escalate(goal, goalPrefix, policy, state,
                $"{decision.Reason}{Environment.NewLine}automatic retry failed: {ex.Message}{Environment.NewLine}checkpoint commit: {checkpoint}{Environment.NewLine}{errorBlock}");
        }

        var refreshedGoal = GetCurrentGoal(goal);
        var refreshedTask = refreshedGoal.Tasks.Single(candidate => candidate.Id == task.Id);
        if (refreshedTask.Status is not (WorkTaskStatus.Assigned or WorkTaskStatus.Pending) ||
            refreshedGoal.Tasks.Any(candidate => candidate.LastProcess is { IsRunning: true }))
            return MakeResult(refreshedGoal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal)),
                    "Worker build check retry was applied; dispatch start awaits a fresh lifecycle observation."));
        return ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
    }
}
