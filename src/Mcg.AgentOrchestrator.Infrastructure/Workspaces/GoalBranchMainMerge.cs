using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalBranchMainMerge
{
    public static GoalWorktreeRebaseResult TryMergeMainIntoGoalBranch(
        string executionDirectory, GoalId goalId,
        AdditiveConflictMergeOptions? additiveMerge = null, string? trunkBranch = null)
    {
        var branch = GoalWorktrees.BranchName(goalId);
        var hasBranch = GoalWorktrees.HasBranch(executionDirectory, goalId);
        var baseBranch = CurrentBranchName(executionDirectory) ?? TrunkBranchName.Resolve(trunkBranch);
        var prefix = GoalWorktreeLayout.Prefix(goalId);
        if (!hasBranch)
            return new(GoalWorktreeRebaseStatus.MissingBranch, branch,
                $"Goal branch {branch} is missing.", [], "goal-recovery");

        var worktree = GoalWorktrees.TryResolve(executionDirectory, goalId);
        if (worktree is null)
            return new(GoalWorktreeRebaseStatus.MissingWorktree, branch,
                $"Goal branch {branch} has no registered worktree.", [], $"workspace create {prefix}");

        if (GitCli.IsWorktreeDirty(worktree))
            return new(GoalWorktreeRebaseStatus.DirtyWorktree, branch,
                $"Goal worktree for {branch} has uncommitted changes; commit or discard them before rebase recovery.",
                [], $"goal-recovery {prefix}");

        if (GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branch).ExitCode == 0)
            return new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, branch,
                $"Branch {branch} can already fast-forward into main; no rebase needed.", [], $"acceptance {prefix}");

        var mainSha = GoalWorktrees.ResolveRequiredRef(executionDirectory, $"{baseBranch}^{{commit}}");
        var oldHead = GoalWorktrees.ResolveRequiredRef(worktree, "HEAD");
        var merge = GitCli.Run(worktree,
            "-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
            "-c", "commit.gpgSign=false", "merge", "--no-ff", "--no-edit",
            "-m", $"Integrate {baseBranch} into {branch} before acceptance", mainSha);
        if (merge.ExitCode == 0)
        {
            var materialized = GoalWorktrees.ValidatePostRebaseMaterialization(worktree, branch, baseBranch, goalId);
            if (materialized.Status == GoalWorktreeRebaseStatus.Rebased)
            {
                SourceSizeRatchetRetightener.RetightenAndCommit(worktree, mainSha, prefix);
                return materialized with { Detail = "merged-main" };
            }
            return materialized;
        }

        var conflictFiles = ConflictPaths(worktree);
        _ = GitCli.Run(worktree, "merge", "--abort");
        var restoredHead = GitCli.Run(worktree, "rev-parse", "--verify", "HEAD");
        var dirty = GitCli.IsWorktreeDirty(worktree);
        if (restoredHead.ExitCode != 0 || restoredHead.Output.Trim() != oldHead || dirty)
            return new(GoalWorktreeRebaseStatus.Failed, branch,
                $"Merge of {baseBranch} into {branch} failed to restore entry state: expected HEAD={oldHead}; " +
                $"HEAD={Collapse(restoredHead.Output)}; head-exit={restoredHead.ExitCode}; dirty={dirty}.",
                conflictFiles, $"goal-recovery {prefix}");

        if (conflictFiles.Length > 0 && additiveMerge is not null &&
            GoalWorktrees.TryMergeAdditiveConflict(worktree, branch, baseBranch, goalId, mainSha, conflictFiles, additiveMerge) is { } merged)
            return merged;
        if (conflictFiles.Length > 0)
            return new(GoalWorktreeRebaseStatus.Conflict, branch,
                $"Merge of {baseBranch} into {branch} found conflicts; branch left unchanged.", conflictFiles,
                $"Create an operator task to resolve conflicts in order: {string.Join(", ", conflictFiles)}");

        var stdErr = Collapse(merge.Error);
        var stdOut = Collapse(merge.Output);
        var streams = string.Join("; ", new[]
        {
            string.IsNullOrWhiteSpace(stdErr) ? null : $"stderr={stdErr}",
            string.IsNullOrWhiteSpace(stdOut) ? null : $"stdout={stdOut}",
        }.Where(part => part is not null));
        var detail = string.IsNullOrWhiteSpace(streams)
            ? $"exit={merge.ExitCode}; no output captured"
            : $"exit={merge.ExitCode}; {streams}";
        return new(GoalWorktreeRebaseStatus.Failed, branch,
            $"Merge of {baseBranch} into {branch} failed with no conflicting paths ({detail}); branch left unchanged.",
            [], $"goal-recovery {prefix}");
    }

    private static string? CurrentBranchName(string directory)
    {
        var result = GitCli.Run(directory, "branch", "--show-current");
        var branch = result.Output.Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : null;
    }

    private static string[] ConflictPaths(string worktree)
    {
        var result = GitCli.Run(worktree, "diff", "--name-only", "--diff-filter=U");
        return result.ExitCode != 0 ? [] : result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Collapse(string value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(" | ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
