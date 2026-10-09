using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class FailedRoundCheckpointPreDispatch(
    AgentOrchestratorKernel kernel, string executionDirectory, string integrationBranch, DispatchWorktreeCommitter? committer = null)
{
    private readonly DispatchWorktreeCommitter _committer = committer ?? new();

    internal DeveloperBranchIntegrationResult IntegrateMainBeforeDeveloperDispatch(Goal goal)
    {
        TryCheckpoint(goal);
        return ConductorDriver.IntegrateMainBeforeDeveloperDispatch(executionDirectory, goal, integrationBranch);
    }

    private void TryCheckpoint(Goal goal)
    {
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null || !GoalWorktreeLayout.IsExclusiveGoalWorktree(worktree, goal.Id))
            return;
        var assigned = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer &&
            task.Status == WorkTaskStatus.Assigned).ToArray();
        if (assigned.Length != 1 || assigned[0].DispatchHistory.Count == 0)
            return;
        var task = assigned[0];
        var dispatch = task.DispatchHistory[^1];
        if (dispatch.FailedRoundCheckpointReceipt is not { } receipt ||
            receipt.DispatchId != BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch) ||
            !string.Equals(Path.GetFullPath(dispatch.WorkingDirectory), Path.GetFullPath(worktree), PathComparison))
            return;
        var inspection = _committer.InspectGoalWorktree(worktree, goal.Id, dispatch.DispatchedAt, forceRefresh: true);
        if (!inspection.IsAvailable || inspection.Evidence.IsClean || receipt.DirtyPaths.Count == 0 ||
            !string.Equals(inspection.Evidence.DirtyStateHash, receipt.DirtyStateHash, StringComparison.Ordinal) ||
            !inspection.Evidence.DirtyPaths.SequenceEqual(receipt.DirtyPaths, StringComparer.Ordinal) ||
            receipt.DirtyPaths.Any(path => !IsContainedPath(worktree, path)))
            return;

        // git commit includes the entire index. Refuse unrelated staged artifacts even when
        // the status inspection filtered them out of the commit-worthy path list.
        var staged = GitCli.Run(worktree, "diff", "--cached", "--name-only", "-z");
        if (!staged.Succeeded || staged.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(path => !receipt.DirtyPaths.Contains(path, StringComparer.Ordinal)))
            return;
        var index = GitCli.Run(worktree, "write-tree");
        if (!index.Succeeded)
        {
            RecordFailure(index.Error);
            return;
        }
        var result = _committer.TryCommitWorktreeEdits(worktree,
            $"checkpoint: preserved uncommitted edits from failed dispatch {receipt.DispatchId}", receipt.DirtyPaths);
        if (!result.Succeeded)
        {
            // Restore staged/unstaged attribution as well as preserving file contents.
            var restored = GitCli.Run(worktree, "read-tree", index.Output.Trim());
            RecordFailure(result.Diagnostic + (restored.Succeeded ? "" : " Index restore failed: " + restored.Error));
            return;
        }
        var head = GitCli.Run(worktree, "rev-parse", "--verify", "HEAD^{commit}");
        if (!head.Succeeded || string.IsNullOrWhiteSpace(head.Output))
            throw new InvalidOperationException("Checkpoint committed, but its resulting HEAD could not be recorded.");
        kernel.RecordFailedRoundCheckpointDecision(goal.Id, task.Id, dispatch.DispatchedAt,
            new(receipt.DispatchId, FailedRoundCheckpointOutcome.Committed, head.Output.Trim(), null));

        void RecordFailure(string diagnostic) => kernel.RecordFailedRoundCheckpointDecision(
            goal.Id, task.Id, dispatch.DispatchedAt,
            new(receipt.DispatchId, FailedRoundCheckpointOutcome.CommitFailed, null, diagnostic));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsContainedPath(string worktree, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
            path.Split('/', '\\').Any(segment => segment == ".."))
            return false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(Path.Combine(worktree, path)).StartsWith(root, PathComparison);
    }
}
