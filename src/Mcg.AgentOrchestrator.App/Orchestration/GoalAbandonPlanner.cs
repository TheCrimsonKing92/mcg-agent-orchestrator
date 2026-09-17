using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalAbandonStepKind
{
    GoalStatus,
    RunningDispatches,
    Worktree,
    BuildLease,
    Retention
}

internal enum GoalAbandonDisposition
{
    Apply,
    Applied,
    Blocked,
    Keep,
    Missing
}

internal sealed record GoalAbandonPlan(
    GoalId GoalId,
    string GoalPrefix,
    GoalStatus GoalStatus,
    string Reason,
    bool DryRun,
    bool CanApply,
    IReadOnlyList<GoalAbandonStep> Steps,
    GoalArtifactRetentionPlan RetentionPlan);

internal sealed record GoalAbandonStep(
    GoalAbandonStepKind Kind,
    GoalAbandonDisposition Disposition,
    string Detail,
    string? SuggestedCommand);

internal static class GoalAbandonPlanner
{
    public static GoalAbandonPlan Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        string reason,
        GoalWorktreeCleanupHooks cleanupHooks,
        bool dryRun = true)
    {
        ArgumentNullException.ThrowIfNull(cleanupHooks);
        var stopReason = NormalizeReason(reason);
        var goalPrefix = goal.Id.Value[..8];
        var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        var dirty = worktree is null ? false : TryIsWorktreeDirty(worktree);
        var runningTasks = goal.Tasks
            .Where(task => task.LastProcess is { IsRunning: true })
            .ToList();
        // The reported lease and the lease Apply actually deletes must describe the same
        // storage namespace, so both read the owner's build storage root.
        var lease = DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id, cleanupHooks.BuildStorageRoot);
        var retention = GoalArtifactRetentionPlanner.Build(
            kernel,
            goal,
            workspace,
            buildStorageRoot: cleanupHooks.BuildStorageRoot);
        var steps = new List<GoalAbandonStep>();

        if (goal.Status == GoalStatus.Completed)
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.GoalStatus,
                GoalAbandonDisposition.Blocked,
                "Completed goals are accepted or awaiting acceptance; use rollback/revert handling instead of abandonment.",
                "retention-plan"));
        }
        else if (IsTerminal(goal.Status))
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.GoalStatus,
                GoalAbandonDisposition.Keep,
                $"Goal is already {goal.Status}.",
                null));
        }
        else
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.GoalStatus,
                dryRun ? GoalAbandonDisposition.Apply : GoalAbandonDisposition.Applied,
                $"Cancel goal with reason: {stopReason}",
                dryRun ? $"abandon-goal {goalPrefix} <reason> --confirm-goal-abandon" : null));
        }

        if (runningTasks.Count > 0)
        {
            var detail = string.Join(
                ", ",
                runningTasks.Select(task => $"{task.Id.Value[..8]} pid={task.LastProcess!.ProcessId}"));
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.RunningDispatches,
                dryRun ? GoalAbandonDisposition.Apply : GoalAbandonDisposition.Applied,
                $"Cancel running dispatch process record(s): {detail}.",
                dryRun ? $"cancel-dispatch {goalPrefix} <task-number>" : null));
        }
        else
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.RunningDispatches,
                GoalAbandonDisposition.Keep,
                "No running dispatch process records.",
                null));
        }

        if (worktree is null)
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.Worktree,
                GoalAbandonDisposition.Missing,
                "No goal worktree is registered.",
                null));
        }
        else if (dirty)
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.Worktree,
                GoalAbandonDisposition.Blocked,
                $"Worktree has uncommitted changes and must be inspected before cleanup: {worktree}",
                $"goal-recovery {goalPrefix}"));
        }
        else
        {
            steps.Add(new GoalAbandonStep(
                GoalAbandonStepKind.Worktree,
                dryRun ? GoalAbandonDisposition.Apply : GoalAbandonDisposition.Applied,
                $"Remove clean worktree; unmerged committed branch work is preserved by git branch retention: {worktree}",
                dryRun ? $"workspace remove {goalPrefix}" : null));
        }

        steps.Add(new GoalAbandonStep(
            GoalAbandonStepKind.BuildLease,
            lease.CanCleanup
                ? dryRun ? GoalAbandonDisposition.Apply : GoalAbandonDisposition.Applied
                : lease.RootExists ? GoalAbandonDisposition.Keep : GoalAbandonDisposition.Missing,
            lease.Detail,
            lease.CanCleanup && dryRun ? "build-lease-cleanup --confirm-build-lease-cleanup" : null));

        steps.Add(new GoalAbandonStep(
            GoalAbandonStepKind.Retention,
            GoalAbandonDisposition.Keep,
            "Preserve logs, journals, transcripts, and context packages as audit evidence; inspect retention-plan for archive decisions.",
            $"retention-plan {goalPrefix}"));

        var canApply = steps.All(step => step.Disposition != GoalAbandonDisposition.Blocked);
        return new GoalAbandonPlan(goal.Id, goalPrefix, goal.Status, stopReason, dryRun, canApply, steps, retention);
    }

    public static GoalAbandonPlan Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        string reason,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        ArgumentNullException.ThrowIfNull(cleanupHooks);
        var before = Build(kernel, goal, workspace, reason, cleanupHooks);
        if (!before.CanApply)
        {
            return before;
        }

        foreach (var task in goal.Tasks.Where(task => task.LastProcess is { IsRunning: true }).ToArray())
        {
            _ = new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
        }

        if (!IsTerminal(goal.Status))
        {
            _ = kernel.CancelGoal(goal.Id, before.Reason);
        }

        GoalOperationJournal.RecordTerminalDisposition(
            workspace.ExecutionDirectory,
            goal,
            new GoalTerminalDisposition(
                GoalTerminalDispositionKind.Retired,
                $"Goal {goal.Id.Value[..8]} was abandoned by operator and is terminal: {before.Reason}"));

        _ = GoalWorktrees.RemoveTerminal(workspace.ExecutionDirectory, goal.Id, kernel, cleanupHooks);

        if (DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id, cleanupHooks.BuildStorageRoot).CanCleanup)
        {
            _ = DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(
                goal.Id,
                out _,
                out _,
                cleanupHooks.BuildStorageRoot);
        }

        return Build(kernel, goal, workspace, reason, cleanupHooks, dryRun: false);
    }

    private static string NormalizeReason(string reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Goal abandon reason cannot be empty.", nameof(reason));
        }

        return normalized;
    }

    private static bool IsTerminal(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static bool TryIsWorktreeDirty(string worktree) => GitCli.IsWorktreeDirty(worktree);
}
