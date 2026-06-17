using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalPruneDisposition
{
    Reapable,
    Pruned,
    SkippedTerminal,
    SkippedNoBranch,
    SkippedUnmergedBranch,
    SkippedLiveWorktree,
    SkippedRunningTasks
}

internal sealed record GoalPruneItem(
    GoalId GoalId,
    string GoalPrefix,
    string Objective,
    GoalStatus Status,
    string BranchName,
    GoalPruneDisposition Disposition,
    string Reason);

internal sealed record GoalsPrunePlan(
    IReadOnlyList<GoalPruneItem> Items,
    bool DryRun)
{
    public int ReapableCount => Items.Count(i => i.Disposition == GoalPruneDisposition.Reapable);
    public int PrunedCount => Items.Count(i => i.Disposition == GoalPruneDisposition.Pruned);
}

internal static class GoalsPrunePlanner
{
    internal const string PruneReason = "goals-prune: branch merged, worktree gone";

    public static GoalsPrunePlan Build(AgentOrchestratorKernel kernel, string executionDirectory)
    {
        return new GoalsPrunePlan(ClassifyGoals(kernel, executionDirectory), DryRun: true);
    }

    public static GoalsPrunePlan Apply(AgentOrchestratorKernel kernel, string executionDirectory)
    {
        var preview = Build(kernel, executionDirectory);
        var applied = new List<GoalPruneItem>();
        foreach (var item in preview.Items)
        {
            if (item.Disposition != GoalPruneDisposition.Reapable)
            {
                applied.Add(item);
                continue;
            }

            kernel.CancelGoal(item.GoalId, PruneReason);
            GoalWorktrees.Remove(executionDirectory, item.GoalId);
            applied.Add(item with { Disposition = GoalPruneDisposition.Pruned });
        }

        return new GoalsPrunePlan(applied, DryRun: false);
    }

    private static List<GoalPruneItem> ClassifyGoals(AgentOrchestratorKernel kernel, string executionDirectory)
    {
        var items = new List<GoalPruneItem>();
        foreach (var goal in kernel.Goals)
        {
            var prefix = goal.Id.Value[..8];
            var branch = GoalWorktrees.BranchName(goal.Id);

            if (goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Completed)
            {
                items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                    GoalPruneDisposition.SkippedTerminal, $"Already {goal.Status}"));
                continue;
            }

            var runningCount = goal.Tasks.Count(t => t.LastProcess is { IsRunning: true });
            if (runningCount > 0)
            {
                items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                    GoalPruneDisposition.SkippedRunningTasks,
                    $"Has {runningCount} running dispatch(es)"));
                continue;
            }

            if (!BranchExists(executionDirectory, branch))
            {
                items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                    GoalPruneDisposition.SkippedNoBranch, $"Branch {branch} does not exist"));
                continue;
            }

            if (!IsBranchFullyMergedIntoHead(executionDirectory, branch))
            {
                items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                    GoalPruneDisposition.SkippedUnmergedBranch,
                    $"Branch {branch} has commits not yet in HEAD"));
                continue;
            }

            if (GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null)
            {
                items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                    GoalPruneDisposition.SkippedLiveWorktree,
                    "Live worktree still exists"));
                continue;
            }

            items.Add(new GoalPruneItem(goal.Id, prefix, goal.Objective, goal.Status, branch,
                GoalPruneDisposition.Reapable,
                "Branch fully merged into HEAD; no live worktree"));
        }

        return items;
    }

    private static bool BranchExists(string executionDirectory, string branch) =>
        GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;

    // Returns true when the branch tip is an ancestor of (or equal to) HEAD, meaning all its
    // commits are already present in the main checkout — i.e., the branch is fully merged.
    private static bool IsBranchFullyMergedIntoHead(string executionDirectory, string branch) =>
        GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", branch, "HEAD").ExitCode == 0;
}
