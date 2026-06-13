using System.Diagnostics;
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
            .Where(goal => goal.Status == GoalStatus.Completed ||
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

        if (goal.Status != GoalStatus.Completed)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal is not completed",
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

    private static bool TryIsDirty(string worktreePath)
    {
        var result = RunGit(worktreePath, "status", "--porcelain");
        return result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Output);
    }

    private static bool BranchExists(string executionDirectory, string branchName)
    {
        return RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}").ExitCode == 0;
    }

    private static bool IsFastForwardable(string executionDirectory, string branchName)
    {
        return RunGit(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branchName).ExitCode == 0;
    }

    private static bool BranchHasDiff(string executionDirectory, string branchName)
    {
        var result = RunGit(executionDirectory, "diff", "--name-only", "HEAD..." + branchName);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitResult(1, string.Empty, "failed to start git");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            return process.WaitForExit(10000)
                ? new GitResult(process.ExitCode, output, error)
                : new GitResult(1, output, "git command timed out");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(1, string.Empty, ex.Message);
        }
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
