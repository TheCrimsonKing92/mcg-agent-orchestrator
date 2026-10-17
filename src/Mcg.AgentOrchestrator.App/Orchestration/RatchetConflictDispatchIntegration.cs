using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class RatchetConflictDispatchIntegration(
    AgentOrchestratorKernel kernel, string executionDirectory, string integrationBranch,
    string conductEventsLogPath, Func<string, OrchestratorBuildCheckResult>? buildCheck = null)
{
    private readonly FailedRoundCheckpointPreDispatch _inner = new(kernel, executionDirectory, integrationBranch);

    internal DeveloperBranchIntegrationResult IntegrateMainBeforeDeveloperDispatch(Goal goal)
    {
        var original = _inner.IntegrateMainBeforeDeveloperDispatch(goal);
        if (original.Status != DeveloperBranchIntegrationStatus.Conflict || original.ConflictPaths.Count != 1 ||
            !string.Equals(original.ConflictPaths[0], SourceSizeRatchet.SourcePath, StringComparison.Ordinal))
            return original;
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktree is null) return original;
        var oldHead = GoalWorktrees.ResolveRequiredRef(worktree, "HEAD");
        var merged = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(executionDirectory, goal.Id,
            ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, goal, conductEventsLogPath), integrationBranch);
        if (merged.Status != GoalWorktreeRebaseStatus.Rebased)
            return merged.Status == GoalWorktreeRebaseStatus.Failed
                ? original with { Message = original.Message + Environment.NewLine + merged.Message } : original;
        var newHead = GoalWorktrees.ResolveRequiredRef(worktree, "HEAD");
        if (newHead == oldHead) return original;

        // Retightening can add one commit after the merge. Bind receipts to the final head,
        // but derive the integrated main identity from the actual merge's second parent.
        var mergeRevision = newHead;
        var subject = GitCli.Run(worktree, "log", "-1", "--format=%s", newHead);
        if (subject.Succeeded && subject.Output.Trim() == SourceSizeRatchetRetightener.CommitSubject)
            mergeRevision = newHead + "^";
        var parents = GitCli.Run(worktree, "rev-list", "--parents", "-n", "1", mergeRevision);
        var ids = parents.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        OrchestratorBuildCheckResult check;
        if (!parents.Succeeded || ids.Length != 3 || ids[1] != oldHead)
            check = new(false, -1, "Resolved merge parent identities could not be verified.");
        else
        {
            try { check = buildCheck is null ? BuildChangedProjects(worktree, oldHead, newHead) : buildCheck(worktree); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { check = new(false, -1, "Build check failed: " + exception.Message); }
        }
        if (!check.Ran || check.ExitCode != 0)
        {
            var restored = TryRestore(worktree, goal.Id, oldHead, newHead);
            var output = VerificationTextBounds.BoundText(check.Output, path: null);
            return original with { Message = original.Message + Environment.NewLine + output +
                (restored ? "" : Environment.NewLine + "Compare-and-swap reset did not restore the original head; operator recovery required.") };
        }
        ResolvedCandidateObligationRebinder.Rebind(kernel, goal, newHead);
        return new(DeveloperBranchIntegrationStatus.Integrated,
            $"Conductor integrated main by ratchet ceiling conflict resolution at {newHead}.", [],
            OriginalCandidateSha: oldHead, IntegratedMainSha: ids[2], ResultingCandidateSha: newHead);
    }

    internal static GoalWorktreeRebaseResult MergeMainBeforeLanding(
        AgentOrchestratorKernel kernel, string executionDirectory, Goal goal,
        AdditiveConflictMergeOptions options, string integrationBranch)
    {
        var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(executionDirectory, goal.Id, options, integrationBranch);
        if (result.Status == GoalWorktreeRebaseStatus.Rebased && result.Detail == "additive-conflict-auto-merge")
        {
            var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id)
                ?? throw new InvalidOperationException("Resolved goal worktree disappeared before obligation rebind.");
            ResolvedCandidateObligationRebinder.Rebind(kernel, goal, GoalWorktrees.ResolveRequiredRef(worktree, "HEAD"));
        }
        return result;
    }

    private static OrchestratorBuildCheckResult BuildChangedProjects(string worktree, string oldHead, string newHead)
    {
        var diff = GitCli.Run(worktree, "diff", "--name-only", "-z", oldHead, newHead);
        if (!diff.Succeeded) return new(false, -1, "Could not determine build scope: " + diff.Error);
        var projects = WorkerBuildEvidenceRequirement.FindRequiredProjects(worktree, AgentRole.Developer,
            diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), []);
        return projects.Count == 0 ? new(false, -1, "Could not determine changed projects for build check.")
            : OrchestratorBuildEvidenceCheck.RunDefault(new(worktree, projects, TimeSpan.FromMinutes(7)));
    }

    private static bool TryRestore(string worktree, GoalId goalId, string oldHead, string newHead)
    {
        var branch = "refs/heads/" + GoalWorktrees.BranchName(goalId);
        var attached = GitCli.Run(worktree, "symbolic-ref", "HEAD");
        if (!attached.Succeeded || attached.Output.Trim() != branch) return false;
        // Never overwrite a successor head: update-ref's old-value argument is the CAS guard.
        var resetRef = GitCli.Run(worktree, "update-ref", branch, oldHead, newHead);
        if (!resetRef.Succeeded) return false;
        var resetTree = GitCli.Run(worktree, "reset", "--hard");
        return resetTree.Succeeded && GoalWorktrees.ResolveRequiredRef(worktree, "HEAD") == oldHead &&
            !GitCli.IsWorktreeDirty(worktree);
    }
}
