using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record StreamCompositionCandidate(GoalId GoalId, string BranchRevision);

public sealed record StreamCompositionBuildOutcome(bool Passed, string? FailingProject, string OutputExcerpt);

public sealed record StreamCompositionResult(
    string CommitRevision,
    string TreeRevision,
    IReadOnlyList<GoalId> ComposedChildren,
    MergeTrainEjection? Ejection,
    StreamCompositionBuildOutcome? BuildCheck = null)
{
    public bool IsReleasable => Ejection is null && BuildCheck is { Passed: true };
}

public static class StreamComposer
{
    public static StreamCompositionResult Compose(
        string executionDirectory,
        GoalId parentGoalId,
        string parentBranchRevision,
        IReadOnlyList<StreamCompositionCandidate> children,
        DateTimeOffset? committerDate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        ArgumentNullException.ThrowIfNull(parentGoalId);
        ArgumentNullException.ThrowIfNull(children);
        var candidates = children.ToArray();
        if (candidates.Length == 0 || candidates.Any(child => child.GoalId == parentGoalId) ||
            candidates.Select(child => child.GoalId).Distinct().Count() != candidates.Length)
            throw new ArgumentException("Composition requires distinct children other than the parent.", nameof(children));

        var root = Path.GetFullPath(executionDirectory);
        var parentRevision = MergeTrainMemberBinding.NormalizeRevision(parentBranchRevision, nameof(parentBranchRevision));
        var branches = candidates.ToDictionary(child => child.GoalId,
            child => MergeTrainMemberBinding.NormalizeRevision(child.BranchRevision, nameof(children)));
        branches.Add(parentGoalId, parentRevision);
        AssertBranchesUnchanged(root, branches, beforeComposition: true);

        var worktreeRoot = Path.Combine(root, GoalWorktrees.DirectoryName);
        Directory.CreateDirectory(worktreeRoot);
        var workspacePath = Path.Combine(worktreeRoot, $"c-{Guid.NewGuid():N}"[..14]);
        try
        {
            var added = GitCli.Run(root, "worktree", "add", "--detach", workspacePath, parentRevision);
            if (added.ExitCode != 0)
                throw new InvalidOperationException($"Failed to create stream composition worktree: {added.Error}");

            var composed = new List<GoalId>(candidates.Length);
            MergeTrainEjection? ejection = null;
            foreach (var child in candidates)
            {
                var step = RebaseOntoPriorHead(root, workspacePath, parentRevision, branches[child.GoalId],
                    child.GoalId, "Stream composition", args => committerDate is null
                        ? GitCli.Run(workspacePath, args)
                        : GitCli.RunWithEnvironment(workspacePath,
                            new Dictionary<string, string>
                            {
                                ["GIT_COMMITTER_DATE"] = FormattableString.Invariant($"@{committerDate.Value.ToUnixTimeSeconds()} +0000")
                            }, args));
                if (step.Ejection is not null)
                {
                    ejection = step.Ejection;
                    break;
                }
                composed.Add(child.GoalId);
            }

            SourceSizeRatchetRetightener.RetightenAndCommit(workspacePath, parentRevision,
                $"compose:{parentGoalId.Value[..8]}:{string.Join('+', composed.Select(id => id.Value[..8]))}", committerDate);
            AssertBranchesUnchanged(root, branches, beforeComposition: false);
            return new StreamCompositionResult(
                GoalWorktrees.ResolveRequiredRef(workspacePath, "HEAD"),
                GoalWorktrees.ResolveRequiredRef(workspacePath, "HEAD^{tree}"),
                Array.AsReadOnly(composed.ToArray()), ejection);
        }
        finally
        {
            GoalWorktrees.RemoveMergeTrainWorkspace(root, workspacePath);
        }
    }

    private static void AssertBranchesUnchanged(
        string root, IReadOnlyDictionary<GoalId, string> branches, bool beforeComposition)
    {
        foreach (var branch in branches)
        {
            var current = GoalWorktrees.ResolveRequiredRef(root, $"refs/heads/{GoalWorktrees.BranchName(branch.Key)}");
            if (!current.Equals(branch.Value, StringComparison.Ordinal))
                throw new InvalidOperationException(beforeComposition
                    ? $"Stream composition member {branch.Key.Value[..8]} is stale before composition."
                    : $"Goal branch {GoalWorktrees.BranchName(branch.Key)} changed while stream composition was active.");
        }
    }

    internal sealed record RebaseStep(string? Head, MergeTrainEjection? Ejection);

    // Both materializers use one rebase/abort/restore implementation; only their loop policy differs.
    internal static RebaseStep RebaseOntoPriorHead(
        string root, string workspacePath, string baseRevision, string candidateRevision,
        GoalId goalId, string restoreSubject, Func<string[], GitCli.GitResult> runRebase)
        => RebaseCandidate(root, workspacePath, baseRevision, candidateRevision, goalId, restoreSubject, runRebase);

    private static RebaseStep RebaseCandidate(
        string root, string workspacePath, string baseRevision, string candidateRevision,
        GoalId goalId, string restoreSubject, Func<string[], GitCli.GitResult> runRebase)
    {
        var priorHead = GoalWorktrees.ResolveRequiredRef(workspacePath, "HEAD");
        var mergeBase = GitCli.Run(root, "merge-base", baseRevision, candidateRevision);
        if (mergeBase.ExitCode != 0 || string.IsNullOrWhiteSpace(mergeBase.Output))
            return new RebaseStep(null, new MergeTrainEjection(goalId,
                MergeTrainEjectionReason.MaterializationFailure, [], $"merge base unavailable: {mergeBase.Error}"));

        var rebase = runRebase([
            "-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
            "rebase", "--merge", "--no-stat", "--onto", priorHead, mergeBase.Output.Trim(), candidateRevision]);
        if (rebase.ExitCode != 0)
        {
            var conflicts = GoalWorktrees.ReadMergeTrainConflictPaths(workspacePath);
            _ = GitCli.Run(workspacePath, "rebase", "--abort");
            var restore = GitCli.Run(workspacePath, "reset", "--hard", priorHead);
            if (restore.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{restoreSubject} conflict abort could not restore the tested prefix: {restore.Error}");
            return new RebaseStep(null, new MergeTrainEjection(goalId,
                MergeTrainEjectionReason.RebaseConflict, conflicts, $"rebase conflict: {rebase.Error}"));
        }
        return new RebaseStep(GoalWorktrees.ResolveRequiredRef(workspacePath, "HEAD"), null);
    }
}
