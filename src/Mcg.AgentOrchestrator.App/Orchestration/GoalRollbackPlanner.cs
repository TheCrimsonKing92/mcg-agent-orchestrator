using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRollbackAcceptanceRange(
    string GoalId,
    string GoalPrefix,
    string GoalBranch,
    string BaseCommit,
    string AcceptedHeadCommit,
    string MainBeforeAcceptance,
    DateTimeOffset AcceptedAt);

internal sealed record GoalRollbackPlan(
    GoalId GoalId,
    string GoalPrefix,
    string Reason,
    bool DryRun,
    bool CanApply,
    string RollbackBranch,
    string Detail,
    string? SuggestedCommand);

internal static class GoalRollbackPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GoalRollbackAcceptanceRange? CapturePendingAcceptance(string executionDirectory, GoalId goalId, string integrationBranch)
    {
        var branch = GoalWorktrees.BranchName(goalId);
        var baseCommit = GitCli.Run(executionDirectory, "merge-base", integrationBranch, branch);
        var head = GitCli.Run(executionDirectory, "rev-parse", branch);
        var main = GitCli.Run(executionDirectory, "rev-parse", integrationBranch);
        if (baseCommit.ExitCode != 0 || head.ExitCode != 0 || main.ExitCode != 0)
        {
            return null;
        }

        return new GoalRollbackAcceptanceRange(
            goalId.Value,
            goalId.Value[..8],
            branch,
            baseCommit.Output.Trim(),
            head.Output.Trim(),
            main.Output.Trim(),
            DateTimeOffset.UtcNow);
    }

    public static void RecordAcceptance(string executionDirectory, GoalRollbackAcceptanceRange range)
    {
        Directory.CreateDirectory(StoreDirectory(executionDirectory));
        File.WriteAllText(StorePath(executionDirectory, new GoalId(range.GoalId)), JsonSerializer.Serialize(range, JsonOptions));
    }

    public static GoalRollbackPlan Build(string executionDirectory, Goal goal, string reason, string integrationBranch, bool dryRun = true)
    {
        var rollbackReason = NormalizeReason(reason);
        var prefix = goal.Id.Value[..8];
        var branch = $"rollback/{prefix}";
        var range = ReadRange(executionDirectory, goal.Id);
        if (range is null)
        {
            return new GoalRollbackPlan(
                goal.Id,
                prefix,
                rollbackReason,
                dryRun,
                CanApply: false,
                branch,
                "No acceptance rollback metadata exists for this goal. Re-verify manually or create a revert branch by hand.",
                null);
        }

        if (BranchExists(executionDirectory, branch))
        {
            return new GoalRollbackPlan(
                goal.Id,
                prefix,
                rollbackReason,
                dryRun,
                CanApply: false,
                branch,
                $"Rollback branch {branch} already exists.",
                $"git switch {branch}");
        }

        return new GoalRollbackPlan(
            goal.Id,
            prefix,
            rollbackReason,
            dryRun,
            CanApply: true,
            branch,
            $"Revert accepted range {range.BaseCommit[..8]}..{range.AcceptedHeadCommit[..8]} from {integrationBranch} into {branch}.",
            dryRun ? $"rollback-goal {prefix} <reason> --confirm-goal-rollback" : null);
    }

    public static GoalRollbackPlan Apply(string executionDirectory, Goal goal, string reason, string integrationBranch)
    {
        var plan = Build(executionDirectory, goal, reason, integrationBranch);
        if (!plan.CanApply)
        {
            return plan;
        }

        var range = ReadRange(executionDirectory, goal.Id)
            ?? throw new InvalidOperationException("Rollback metadata disappeared before apply.");

        var switchMain = GitCli.Run(executionDirectory, "switch", integrationBranch);
        if (switchMain.ExitCode != 0)
            throw new InvalidOperationException($"Failed to switch to {integrationBranch}: {switchMain.Error}");
        var createBranch = GitCli.Run(executionDirectory, "switch", "-c", plan.RollbackBranch);
        if (createBranch.ExitCode != 0)
            throw new InvalidOperationException($"Failed to create rollback branch: {createBranch.Error}");
        var revert = GitCli.Run(executionDirectory, "revert", "--no-commit", $"{range.BaseCommit}..{range.AcceptedHeadCommit}");
        if (revert.ExitCode != 0)
        {
            throw new InvalidOperationException($"Rollback revert conflicted on {plan.RollbackBranch}: {revert.Error}");
        }

        var commitResult = GitCli.Run(executionDirectory, "commit", "-m", $"Rollback goal {plan.GoalPrefix}: {plan.Reason}");
        if (commitResult.ExitCode != 0)
            throw new InvalidOperationException($"Failed to commit rollback: {commitResult.Error}");
        return Build(executionDirectory, goal, reason, integrationBranch, dryRun: false) with
        {
            CanApply = true,
            Detail = $"Created rollback branch {plan.RollbackBranch} reverting accepted range {range.BaseCommit[..8]}..{range.AcceptedHeadCommit[..8]}.",
            SuggestedCommand = $"git switch {plan.RollbackBranch}"
        };
    }

    private static GoalRollbackAcceptanceRange? ReadRange(string executionDirectory, GoalId goalId)
    {
        var path = StorePath(executionDirectory, goalId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<GoalRollbackAcceptanceRange>(File.ReadAllText(path), JsonOptions)
            : null;
    }

    private static string StoreDirectory(string executionDirectory) =>
        Path.Combine(executionDirectory, ".orchestrator", "rollback");

    private static string StorePath(string executionDirectory, GoalId goalId) =>
        Path.Combine(StoreDirectory(executionDirectory), $"{goalId.Value[..8]}.json");

    private static bool BranchExists(string executionDirectory, string branch) =>
        GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;

    private static string NormalizeReason(string reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Goal rollback reason cannot be empty.", nameof(reason));
        }

        return normalized;
    }

}
