using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum AcceptanceQueueDisposition
{
    Ready,
    Held,
    Blocked
}

public sealed record AcceptanceQueuePlan(
    AutonomyPolicy Policy,
    IReadOnlyList<AcceptanceQueueItem> Items)
{
    public int ReadyCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Ready);
    public int HeldCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Held);
    public int BlockedCount => Items.Count(item => item.Disposition == AcceptanceQueueDisposition.Blocked);
}

public sealed record AcceptanceQueueItem(
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

public static class AcceptanceQueuePlanner
{
    public static AcceptanceQueuePlan Build(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        AutonomyPolicy policy, string integrationBranch) => BuildCore(kernel, executionDirectory, policy, integrationBranch, null);

    internal static AcceptanceQueuePlan Build(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        AutonomyPolicy policy, string integrationBranch,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner) =>
        BuildCore(kernel, executionDirectory, policy, integrationBranch, gitRunner);

    private static AcceptanceQueuePlan BuildCore(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        AutonomyPolicy policy, string integrationBranch,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner)
    {
        var goals = kernel.Goals.ToArray();
        if (goals.Length == 0)
        {
            return new AcceptanceQueuePlan(policy, []);
        }

        var gitFacts = GoalGitFactIndex.Build(executionDirectory, integrationBranch, gitRunner);
        Func<string, IReadOnlyList<string>, GitCli.GitResult> probeRunner =
            gitRunner ?? ((directory, args) => GitCli.Run(directory, args.ToArray()));
        var items = goals
            .Where(goal => goal.Status is GoalStatus.Verified or GoalStatus.Completed ||
                GoalWorktrees.TryResolve(executionDirectory, goal.Id) is not null ||
                gitFacts.HasGoalBranch(GoalWorktrees.BranchName(goal.Id)))
            .OrderBy(goal => FirstTimelineAt(goal) ?? DateTimeOffset.MaxValue)
            .ThenBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .Select(goal => BuildItem(goal, executionDirectory, policy, gitFacts, probeRunner))
            .ToArray();

        return new AcceptanceQueuePlan(policy, items);
    }

    private static AcceptanceQueueItem BuildItem(
        Goal goal,
        string executionDirectory,
        AutonomyPolicy policy,
        GoalGitFactIndex gitFacts,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        var goalPrefix = goal.Id.Value[..8];
        var branchName = GoalWorktrees.BranchName(goal.Id);
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var hasBranch = gitFacts.HasGoalBranch(branchName);
        var diff = BranchHasDiff(executionDirectory, branchName, gitRunner);
        ProbeResult? status = worktreePath is null ? null : TryIsDirty(worktreePath, gitRunner);
        var hasDiff = diff.Value;
        bool? dirty = status?.Value;
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

        if (status?.FailureReason is { } statusFailure)
        {
            return Item(AcceptanceQueueDisposition.Blocked, statusFailure, $"goal-recovery {goalPrefix}");
        }

        if (dirty == true)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "worktree has uncommitted changes",
                $"goal-recovery {goalPrefix}");
        }

        if (diff.FailureReason is { } diffFailure)
        {
            return Item(AcceptanceQueueDisposition.Blocked, diffFailure, $"goal-recovery {goalPrefix}");
        }

        if (!hasDiff)
        {
            return Item(
                AcceptanceQueueDisposition.Blocked,
                "goal branch has no diff against main",
                $"goal-recovery {goalPrefix}");
        }

        var fastForward = IsFastForwardable(executionDirectory, branchName, gitRunner);
        if (fastForward.FailureReason is { } fastForwardFailure)
        {
            return Item(AcceptanceQueueDisposition.Blocked, fastForwardFailure, $"goal-recovery {goalPrefix}");
        }

        if (!fastForward.Value)
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

    private readonly record struct ProbeResult(bool Value, string? FailureReason = null);

    private static ProbeResult TryIsDirty(
        string worktreePath,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        string[] args = ["status", "--porcelain=v1", "--untracked-files=all"];
        var result = gitRunner(worktreePath, args);
        return ProbeFailed(result)
            ? new ProbeResult(true, Failure(args, result))
            : new ProbeResult(GitCli.ParseCommitWorthyStatusPaths(result.Output).Length > 0);
    }

    private static ProbeResult IsFastForwardable(
        string executionDirectory,
        string branchName,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        string[] args = ["merge-base", "--is-ancestor", "HEAD", branchName];
        var result = gitRunner(executionDirectory, args);
        return result.ExitCode is not (0 or 1) || !result.ProcessStarted || result.DrainTimedOut
            ? new ProbeResult(false, Failure(args, result))
            : new ProbeResult(result.ExitCode == 0);
    }

    private static ProbeResult BranchHasDiff(
        string executionDirectory,
        string branchName,
        Func<string, IReadOnlyList<string>, GitCli.GitResult> gitRunner)
    {
        string[] args = ["diff", "--name-only", "HEAD..." + branchName];
        var result = gitRunner(executionDirectory, args);
        return ProbeFailed(result)
            ? new ProbeResult(false, Failure(args, result))
            : new ProbeResult(!string.IsNullOrWhiteSpace(result.Output));
    }

    private static bool ProbeFailed(GitCli.GitResult result) =>
        result.ExitCode != 0 || !result.ProcessStarted || result.DrainTimedOut;

    private static string Failure(IReadOnlyList<string> args, GitCli.GitResult result)
    {
        var firstError = result.Error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "(no stderr)";
        return $"git probe failed: git {string.Join(' ', args)} exited {result.ExitCode}: {firstError}";
    }
}
