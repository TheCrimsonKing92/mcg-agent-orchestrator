using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record LandingResult(
    string GoalId,
    string GoalPrefix,
    LandingDecision Decision,
    string IntegrationBranch,
    bool MainAdvanced,
    string Message);

internal static class LandingExecutor
{
    public const string IntegrationBranchName = "integration";
    private const string TempWorktreeDirName = ".orchestrator-integration-tmp";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    public static LandingResult Execute(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace)
    {
        var executionDirectory = workspace.ExecutionDirectory;
        var goalPrefix = goal.Id.Value[..8];
        var goalBranch = GoalWorktrees.BranchName(goal.Id);

        if (!BranchExists(executionDirectory, goalBranch))
        {
            throw new InvalidOperationException(
                $"Goal branch '{goalBranch}' does not exist. Create the workspace first with: workspace create {goalPrefix}");
        }

        EnsureIntegrationBranch(executionDirectory);

        var tempPath = Path.Combine(executionDirectory, TempWorktreeDirName);

        // Remove any leftover temp worktree from a prior interrupted run.
        if (IsRegisteredWorktree(executionDirectory, tempPath))
        {
            RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
        }

        bool mergeSucceeded;
        try
        {
            mergeSucceeded = MergeGoalIntoIntegration(executionDirectory, tempPath, goalBranch);
        }
        finally
        {
            if (IsRegisteredWorktree(executionDirectory, tempPath))
            {
                RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
            }
        }

        if (!mergeSucceeded)
        {
            var conflictReason = $"merge conflict integrating {goalBranch} into {IntegrationBranchName}";
            OperatorInbox.RecordLandingEscalation(workspace, goal, conflictReason, IntegrationBranchName);
            var conflictDecision = new LandingDecision.Escalate(conflictReason);
            return new LandingResult(goal.Id.Value, goalPrefix, conflictDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {conflictReason}");
        }

        var changedFiles = GetChangedFiles(executionDirectory, goalBranch);
        var changeSummary = RepositoryChangeClassifier.Classify(changedFiles);
        var acceptancePassed = kernel.BuildGoalAcceptanceSummary(goal.Id).IsAccepted;
        var cleanFastForward = IsIntegrationFastForwardableIntoMain(executionDirectory);
        var failureCount = CountFailedVerifications(goal);

        var inputs = new LandingInputs(changeSummary, acceptancePassed, cleanFastForward, failureCount);
        var decision = LandingDecisionEngine.Decide(inputs);

        if (decision is LandingDecision.Promote)
        {
            var merge = RunGit(executionDirectory, "merge", "--ff-only", IntegrationBranchName);
            if (merge.ExitCode != 0)
            {
                var unexpectedReason = $"integration->main fast-forward failed: {merge.Error}";
                OperatorInbox.RecordLandingEscalation(workspace, goal, unexpectedReason, IntegrationBranchName);
                var fallback = new LandingDecision.Escalate(unexpectedReason);
                return new LandingResult(goal.Id.Value, goalPrefix, fallback, IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {unexpectedReason}");
            }

            return new LandingResult(goal.Id.Value, goalPrefix, decision, IntegrationBranchName,
                true, $"Promoted: {goalBranch} integrated via {IntegrationBranchName} into main.");
        }

        var escalate = (LandingDecision.Escalate)decision;
        OperatorInbox.RecordLandingEscalation(workspace, goal, escalate.Reason, IntegrationBranchName);
        return new LandingResult(goal.Id.Value, goalPrefix, decision, IntegrationBranchName,
            false, $"Parked on {IntegrationBranchName}: {escalate.Reason}");
    }

    private static void EnsureIntegrationBranch(string executionDirectory)
    {
        if (BranchExists(executionDirectory, IntegrationBranchName))
        {
            return;
        }

        var result = RunGit(executionDirectory, "branch", IntegrationBranchName, "main");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create '{IntegrationBranchName}' branch from main: {result.Error}");
        }
    }

    private static bool MergeGoalIntoIntegration(
        string executionDirectory,
        string tempPath,
        string goalBranch)
    {
        var add = RunGit(executionDirectory, "worktree", "add", tempPath, IntegrationBranchName);
        if (add.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create integration worktree at '{tempPath}': {add.Error}");
        }

        var merge = RunGit(tempPath, "merge", "--no-ff", goalBranch, "-m", $"Integrate {goalBranch}");
        return merge.ExitCode == 0;
    }

    private static bool IsIntegrationFastForwardableIntoMain(string executionDirectory)
    {
        // Exits 0 if main is an ancestor of integration — fast-forward from main to integration tip is possible.
        return RunGit(executionDirectory, "merge-base", "--is-ancestor", "main", IntegrationBranchName).ExitCode == 0;
    }

    private static bool BranchExists(string executionDirectory, string branch)
    {
        return RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path)
    {
        var result = RunGit(executionDirectory, "worktree", "list", "--porcelain");
        if (result.ExitCode != 0)
        {
            return false;
        }

        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => Path.GetFullPath(line["worktree ".Length..].Trim()).TrimEnd(Path.DirectorySeparatorChar))
            .Any(wt => string.Equals(wt, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetChangedFiles(string executionDirectory, string goalBranch)
    {
        var result = RunGit(executionDirectory, "diff", "--name-only", $"main...{goalBranch}");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static int CountFailedVerifications(Goal goal)
    {
        return goal.Tasks.Sum(t => t.VerificationHistory.Count(v => !v.Succeeded));
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
            ?? throw new InvalidOperationException("Failed to start git process.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} timed out.");
        }

        return new GitResult(process.ExitCode, output.Trim(), error.Trim());
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
