using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AcceptanceQueueDisposition
{
    Ready,
    Held,
    Blocked
}

internal sealed record AcceptanceQueuePlan(
    AutonomyPolicy Policy,
    IReadOnlyList<AcceptanceQueueItem> Items)
{
    public int ReadyCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Ready);
    public int HeldCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Held);
    public int BlockedCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Blocked);
}

internal sealed record AcceptanceQueueItem(
    GoalId GoalId,
    string GoalPrefix,
    string Objective,
    GoalStatus Status,
    AcceptanceQueueDisposition Disposition,
    string BranchName,
    string? WorktreePath,
    bool HasBranchDiff,
    bool? WorktreeDirty,
    bool AcceptanceAllowed,
    bool CleanupAllowed,
    string Reason,
    string SuggestedCommand);

internal static class AcceptanceQueuePlanner
{
    public static AcceptanceQueuePlan Build(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        AutonomyPolicy policy)
    {
        var items = kernel.Goals
            .Where(goal => goal.Status is GoalStatus.Verified or GoalStatus.Completed ||
                GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null ||
                BranchExists(executionDirectory, GoalWorktrees.BranchName(goal.Id)))
            .OrderBy(goal => FirstTimelineAt(goal) ?? DateTimeOffset.MaxValue)
            .ThenBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .Select(goal => BuildItem(goal, executionDirectory, policy))
            .ToArray();

        return new AcceptanceQueuePlan(policy, items);
    }

    private static AcceptanceQueueItem BuildItem(
        Goal goal,
        string executionDirectory,
        AutonomyPolicy policy)
    {
        var goalPrefix = goal.Id.Value[..8];
        var branchName = GoalWorktrees.BranchName(goal.Id);
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var hasBranch = BranchExists(executionDirectory, branchName);
        var hasDiff = BranchHasDiff(executionDirectory, branchName);
        bool? dirty = worktreePath is null ? null : TryIsDirty(worktreePath);
        var acceptanceAllowed = policy.Allows(AutonomyAction.Acceptance);
        var cleanupAllowed = policy.Allows(AutonomyAction.WorkspaceCleanup);

        if (goal.Status != GoalStatus.Verified)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal is not verified",
                $"monitor {goalPrefix}");
        }

        if (worktreePath is null)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal has no registered workspace",
                $"workspace create {goalPrefix}");
        }

        if (!hasBranch)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal branch is missing",
                $"goal-recovery {goalPrefix}");
        }

        if (dirty == true)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "worktree has uncommitted changes",
                $"goal-recovery {goalPrefix}");
        }

        if (!hasDiff)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal branch has no diff against main",
                $"goal-recovery {goalPrefix}");
        }

        if (!IsFastForwardable(executionDirectory, branchName))
        {
            return Item(
                AcceptanceQueueDisposition.Held,
                "goal branch cannot fast-forward because main has advanced or branches diverged",
                $"workspace rebase {goalPrefix}");
        }

        if (!acceptanceAllowed || !cleanupAllowed)
        {
            return Item(
                AcceptanceQueueDisposition.Held,
                $"policy '{policy.Name}' blocks irreversible acceptance or cleanup",
                $"acceptance-queue --apply --autonomy {AutonomyPolicy.SupervisedAuto.Name} --confirm-acceptance-queue");
        }

        return Item(
            AcceptanceQueueDisposition.Ready,
            "ready for sequential acceptance and workspace cleanup",
            $"acceptance {goalPrefix} && workspace remove {goalPrefix}");

        AcceptanceQueueItem Item(
            AcceptanceQueueDisposition disposition,
            string reason,
            string suggestedCommand)
        {
            return new AcceptanceQueueItem(
                goal.Id,
                goalPrefix,
                goal.Objective,
                goal.Status,
                disposition,
                branchName,
                worktreePath,
                hasDiff,
                dirty,
                acceptanceAllowed,
                cleanupAllowed,
                reason,
                suggestedCommand);
        }
    }

    private static DateTimeOffset? FirstTimelineAt(Goal goal)
    {
        return goal.Timeline.Count == 0 ? null : goal.Timeline.Min(evt => evt.OccurredAt);
    }

    private static bool TryIsDirty(string worktreePath) => GitCli.IsWorktreeDirty(worktreePath);

    private static bool BranchExists(string executionDirectory, string branchName)
    {
        return GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}").ExitCode == 0;
    }

    private static bool IsFastForwardable(string executionDirectory, string branchName)
    {
        return GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branchName).ExitCode == 0;
    }

    private static bool BranchHasDiff(string executionDirectory, string branchName)
    {
        var result = GitCli.Run(executionDirectory, "diff", "--name-only", "HEAD..." + branchName);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }
}
