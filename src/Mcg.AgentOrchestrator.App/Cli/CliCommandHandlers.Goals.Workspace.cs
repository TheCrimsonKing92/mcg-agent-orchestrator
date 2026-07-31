using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool HandleWorkspaceCommand(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (IsHelpRequested(parts))
    {
        CliCommandHelp.TryPrintStartupHelp(parts);
        return false;
    }

    var action = parts.Count > 1 ? parts[1] : null;
    var goalPrefix = GetFirstNonFlagArgument(parts, startIndex: 2);

    var normalizedAction = (action ?? "status").ToLowerInvariant();
    if (normalizedAction is not ("status" or "create" or "merge" or "rebase" or "remove"))
    {
        throw new ArgumentException("Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
    }
    var forceTerminalCleanup = HasCliConfirmation(parts, "--force-terminal-cleanup");
    if (forceTerminalCleanup && normalizedAction != "remove")
    {
        throw new ArgumentException("--force-terminal-cleanup is valid only with workspace remove.");
    }

    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
    context.CurrentGoal = goal;
    var executionDirectory = context.Workspace.ExecutionDirectory;
    var branch = context.Worktrees.BranchName(goal.Id);
    switch (normalizedAction)
    {
        case "status":
            var existing = context.Worktrees.TryResolve(executionDirectory, goal.Id);
            Console.WriteLine(existing is null
                ? $"Goal has no workspace. Create one with: workspace create (branch {branch})"
                : $"Workspace: {existing} (branch {branch})");
            return false;

        case "create":
            Console.WriteLine($"Workspace: {context.Worktrees.Ensure(executionDirectory, goal.Id)} (branch {branch})");
            return false;

        case "merge":
            var engineHealth = PostLandingCanaryFactory.CreateCircuit(context.Workspace).Read();
            if (!engineHealth.AllowsAcceptance)
            {
                Console.WriteLine(
                    $"Workspace merge blocked: acceptance engine circuit is {engineHealth.Health}. " +
                    "Repair it, then run acceptance-engine clear <note>.");
                return false;
            }

            var merge = context.Worktrees.TryFastForwardMerge(
                executionDirectory,
                goal.Id,
                () => PostLandingCanaryFactory.BuildMutationBlockReason(context.Workspace));
            Console.WriteLine(merge is null
                ? "Goal has no workspace branch to merge."
                : FormatWorkspaceMerge(merge));
            if (merge?.FastForwarded == true)
            {
                var mergeChangedFiles = merge.ChangedFiles
                    ?? throw new InvalidOperationException(
                        "Workspace merge advanced main without an authoritative changed-file receipt.");
                var landingSha = GitCli.Run(executionDirectory, "rev-parse", "HEAD");
                if (!landingSha.Succeeded || string.IsNullOrWhiteSpace(landingSha.Output))
                {
                    throw new InvalidOperationException(
                        $"Post-landing canary could not resolve the merged main SHA: {landingSha.Error}");
                }

                PostLandingCanaryFactory.CreateDefault(context.Workspace)
                    .HandleLanding(new ConductorLandingReceipt(
                        goal.Id.Value,
                        mergeChangedFiles,
                        landingSha.Output.Trim()));
            }
            return false;

        case "rebase":
            Console.WriteLine(FormatWorkspaceRebase(context.Worktrees.TryRebaseOntoMain(executionDirectory, goal.Id)));
            return false;

        case "remove":
            var policy = ResolveCliAutonomyPolicy(parts);
            EnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "workspace remove");
            GoalOperationJournal.Begin(executionDirectory, goal, "workspace:remove", "Removing goal workspace.");
            GoalWorktreeRemoveResult removeResult;
            try
            {
                removeResult = forceTerminalCleanup
                    ? context.Worktrees.RemoveTerminalNow(executionDirectory, goal.Id, context.Kernel)
                    : context.Worktrees.Remove(executionDirectory, goal.Id, context.Kernel);
            }
            catch (InvalidOperationException ex)
            {
                GoalOperationJournal.Failed(executionDirectory, goal, "workspace:remove", ex.Message);
                throw;
            }

            PrintWorkspaceRemoveResult(removeResult);
            if (removeResult.IsComplete)
            {
                GoalOperationJournal.Completed(executionDirectory, goal, "workspace:remove", removeResult.Message);
                if (TryReconcileLandedCleanedAcceptance(context, goal, "workspace remove", out _, cleanupEvidenceRecorded: true))
                {
                    context.EventWriter.AppendCleanedUp(goal.Id);
                    return true;
                }
            }
            else if (TryCompleteLandedBranchOnlyWorkspaceRemove(context, goal, removeResult, out var completedRemoveDetail))
            {
                GoalOperationJournal.Completed(executionDirectory, goal, "workspace:remove", completedRemoveDetail);
                if (TryReconcileLandedCleanedAcceptance(context, goal, "workspace remove", out _, cleanupEvidenceRecorded: true))
                {
                    context.EventWriter.AppendCleanedUp(goal.Id);
                    return true;
                }
            }
            else
            {
                GoalOperationJournal.Failed(executionDirectory, goal, "workspace:remove", removeResult.Message);
            }
            return false;

        default:
            throw new ArgumentException("Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
    }
}

private static void PrintConductUsage()
{
    Console.WriteLine(CliCommandHelp.ConductUsage);
    Console.WriteLine("  -h, --help  Show this help.");
}

private static void PrintWorkspaceUsage()
{
    Console.WriteLine(CliCommandHelp.WorkspaceUsage);
    Console.WriteLine("  -h, --help  Show this help.");
}

private static bool TryCompleteLandedBranchOnlyWorkspaceRemove(
    CliExecutionContext context,
    Goal goal,
    GoalWorktreeRemoveResult removeResult,
    out string detail)
{
    detail = string.Empty;
    var executionDirectory = context.Workspace.ExecutionDirectory;
    var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
    if (context.Worktrees.TryResolve(executionDirectory, goal.Id) is not null ||
        Directory.Exists(worktreePath))
    {
        return false;
    }

    var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
    var hasLandingEvidence = journal.LatestByOperation.Any(entry =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));
    if (!hasLandingEvidence)
    {
        return false;
    }

    var branch = context.Worktrees.BranchName(goal.Id);
    var branchExists = GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
    if (branchExists.ExitCode != 0)
    {
        detail = $"Removed workspace. {removeResult.Message}";
        return true;
    }

    var deleteBranch = GitCli.Run(executionDirectory, "branch", "-D", branch);
    if (deleteBranch.ExitCode != 0)
    {
        return false;
    }

    detail = $"Removed workspace and cleaned landed branch {branch}.";
    return true;
}

private static string ResolveWorktreeHead(string worktreePath)
{
    var result = GitCli.Run(worktreePath, "rev-parse", "HEAD");
    if (!result.Succeeded)
    {
        throw new InvalidOperationException($"Failed to resolve worktree HEAD: {result.Error}");
    }

    return result.Output.Trim();
}

// Deterministic chorekeeping: dispatch owns workspace creation so the operator never hand-runs
// `workspace create` before dispatching. Idempotent — a no-op when the worktree already exists.
// Without this, ResolveExecutionDirectory silently falls back to the repo root and a dispatch
// would prepare context artifacts into the main checkout.
private static void EnsureGoalWorkspaceForDispatch(CliExecutionContext context, Goal goal)
{
    if (context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        return;
    }

    // Deterministic creation needs a git work tree to host the worktree; outside a git repo,
    // fall back to the existing execution-directory resolution rather than failing the dispatch.
    if (!context.Worktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        return;
    }

    var branch = context.Worktrees.BranchName(goal.Id);
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
    var path = context.Worktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:create", path);
    Console.WriteLine($"Workspace auto-created: {path} (branch {branch})");
}

// Post-merge cleanup is durable debt, not an acceptance gate. The conduct loop's terminal
// sweep consumes the cleanup-needed record with normal backoff.
private static void CleanupGoalWorkspaceAfterMerge(
    CliExecutionContext context, Goal goal, AutonomyPolicy policy, bool keepWorkspace)
{
    var goalPrefix = goal.Id.Value[..8];
    if (keepWorkspace)
    {
        Console.WriteLine($"Workspace kept (--keep-workspace). Remove later with: workspace remove {goalPrefix}");
        return;
    }

    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "workspace cleanup", out _))
    {
        Console.WriteLine(
            $"Workspace cleanup deferred by policy. Retry with: conduct {goalPrefix} --loop");
    }

    RecordDeferredGoalCleanup(context, goal, "remove:acceptance-deferred", "acceptance");
}

private static bool JournalAutoCloseSourceBacklogItem(CliExecutionContext context, Goal? goal)
{
    if (goal?.SourceBacklogItemId is null)
    {
        return false;
    }

    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "conductor:backlog-close", "Closing linked source backlog item.");
    var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(
        goal,
        context.Workspace.BacklogStorePath,
        Console.WriteLine,
        context.Kernel,
        context.Workspace.ExecutionDirectory);
    GoalOperationJournal.Completed(
        context.Workspace.ExecutionDirectory,
        goal,
        "conductor:backlog-close",
        closed ? "Closed linked source backlog item." : "No linked source backlog item closed.");
    return closed;
}

private static void PrintWorkspaceRemoveResult(GoalWorktreeRemoveResult result)
{
    Console.WriteLine(result.Message);
    if (result.LeftoverPath is not null)
    {
        Console.WriteLine($"Leftover path: {result.LeftoverPath}");
    }

    if (result.LockHolders.Count > 0)
    {
        Console.WriteLine("Likely lock holders:");
        foreach (var holder in result.LockHolders)
        {
            var detail = holder.CommandLine is not null
                ? $": {holder.CommandLine}"
                : " (command line unavailable)";
            Console.WriteLine($"  {holder.ProcessName} (PID {holder.ProcessId}){detail}");
        }
    }

    if (result.CleanupBackoff is not null)
    {
        Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(result.CleanupBackoff)}");
    }

    if (result.ResumeCommand is not null)
    {
        Console.WriteLine($"Resume: {result.ResumeCommand}");
    }
}

private static void RecordDeferredGoalCleanup(string executionDirectory, GoalId goalId, string reason)
{
    var backoff = GoalWorktrees.RecordGoalCleanupNeeded(executionDirectory, goalId, reason);
    if (backoff is not null)
    {
        Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(backoff)}");
    }
}

private static void RecordDeferredGoalCleanup(CliExecutionContext context, Goal goal, string reason, string source)
{
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "conductor:cleanup", $"Deferred cleanup after {source}.");
    GoalWorktreeCleanupBackoff? backoff;
    try
    {
        backoff = GoalWorktrees.RecordGoalCleanupNeeded(context.Workspace.ExecutionDirectory, goal.Id, reason);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "conductor:cleanup", $"Deferred cleanup after {source}: cleanup-needed unavailable: {ex.Message}");
        Console.WriteLine($"Workspace cleanup deferred: conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop");
        Console.WriteLine($"Cleanup blocker: cleanup-needed unavailable: {ex.Message}");
        return;
    }

    var detail = backoff is null ? "cleanup-needed" : GoalWorktrees.FormatCleanupBackoff(backoff);
    GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "conductor:cleanup", $"Deferred cleanup after {source}: {detail}");
    Console.WriteLine($"Workspace cleanup deferred: conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop");
    if (backoff is not null)
    {
        Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(backoff)}");
    }
}

private static void PrintGoalCleanupBackoffStatus(string executionDirectory, GoalId goalId)
{
    var backoff = GoalWorktrees.TryGetCleanupBackoff(executionDirectory, goalId);
    if (backoff is null)
        return;

    Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(backoff)}");
    Console.WriteLine($"Cleanup retry: conduct {goalId.Value[..8].ToLowerInvariant()} --loop");
}

private static string FormatWorkspaceMerge(GoalWorktreeMergeResult merge)
{
    return merge.FastForwarded
        ? merge.Message
        : $"{merge.Message} command: {merge.SuggestedCommand}";
}

private static string FormatWorkspaceRebase(GoalWorktreeRebaseResult rebase)
{
    var text = rebase.Message;
    if (rebase.ConflictFiles.Count > 0)
    {
        text += $"{Environment.NewLine}Conflict files:";
        foreach (var file in rebase.ConflictFiles)
        {
            text += $"{Environment.NewLine}  {file}";
        }
    }

    if (!string.IsNullOrWhiteSpace(rebase.SuggestedCommand))
    {
        text += $"{Environment.NewLine}Next: {rebase.SuggestedCommand}";
    }

    return text;
}
}
