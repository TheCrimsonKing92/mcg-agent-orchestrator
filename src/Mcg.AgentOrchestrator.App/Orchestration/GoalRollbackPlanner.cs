using System.Diagnostics;
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

    public static GoalRollbackAcceptanceRange? CapturePendingAcceptance(string executionDirectory, GoalId goalId)
    {
        var branch = GoalWorktrees.BranchName(goalId);
        var baseCommit = RunGit(executionDirectory, "merge-base", "main", branch);
        var head = RunGit(executionDirectory, "rev-parse", branch);
        var main = RunGit(executionDirectory, "rev-parse", "main");
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

    public static GoalRollbackPlan Build(string executionDirectory, Goal goal, string reason, bool dryRun = true)
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
            $"Revert accepted range {range.BaseCommit[..8]}..{range.AcceptedHeadCommit[..8]} from main into {branch}.",
            dryRun ? $"rollback-goal {prefix} <reason> --confirm-goal-rollback" : null);
    }

    public static GoalRollbackPlan Apply(string executionDirectory, Goal goal, string reason)
    {
        var plan = Build(executionDirectory, goal, reason);
        if (!plan.CanApply)
        {
            return plan;
        }

        var range = ReadRange(executionDirectory, goal.Id)
            ?? throw new InvalidOperationException("Rollback metadata disappeared before apply.");

        EnsureGit(RunGit(executionDirectory, "switch", "main"), "switch to main");
        EnsureGit(RunGit(executionDirectory, "switch", "-c", plan.RollbackBranch), "create rollback branch");
        var revert = RunGit(executionDirectory, "revert", "--no-commit", $"{range.BaseCommit}..{range.AcceptedHeadCommit}");
        if (revert.ExitCode != 0)
        {
            throw new InvalidOperationException($"Rollback revert conflicted on {plan.RollbackBranch}: {revert.Error}");
        }

        EnsureGit(RunGit(executionDirectory, "commit", "-m", $"Rollback goal {plan.GoalPrefix}: {plan.Reason}"), "commit rollback");
        return Build(executionDirectory, goal, reason, dryRun: false) with
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
        RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;

    private static string NormalizeReason(string reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Goal rollback reason cannot be empty.", nameof(reason));
        }

        return normalized;
    }

    private static void EnsureGit(GitResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to {operation}: {result.Error}");
        }
    }

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return new GitResult(process.ExitCode, output, error);
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
