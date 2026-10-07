using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class FollowerGateWorkspace : IDisposable
{
    private readonly string _executionDirectory;
    private readonly GoalWorktreeCleanupHooks _cleanupHooks;
    private bool _disposed;

    internal FollowerGateWorkspace(string executionDirectory, string path, string testedCommitRevision,
        string testedTreeRevision, string baseMainRevision, GoalId leaderGoalId,
        string leaderCandidateRevision, string leaderCandidateTree, GoalId followerGoalId,
        string followerBranchHead, GoalWorktreeCleanupHooks cleanupHooks)
    {
        _executionDirectory = executionDirectory;
        _cleanupHooks = cleanupHooks;
        Path = path;
        TestedCommitRevision = testedCommitRevision;
        TestedTreeRevision = testedTreeRevision;
        BaseMainRevision = baseMainRevision;
        LeaderGoalId = leaderGoalId;
        LeaderCandidateRevision = leaderCandidateRevision;
        LeaderCandidateTree = leaderCandidateTree;
        FollowerGoalId = followerGoalId;
        FollowerBranchHead = followerBranchHead;
    }

    public string Path { get; }
    public string TestedCommitRevision { get; }
    public string TestedTreeRevision { get; }
    public string BaseMainRevision { get; }
    public GoalId LeaderGoalId { get; }
    public string LeaderCandidateRevision { get; }
    public string LeaderCandidateTree { get; }
    public GoalId FollowerGoalId { get; }
    public string FollowerBranchHead { get; }

    public void AssertFollowerBranchUnchanged()
    {
        var live = GoalWorktrees.ResolveRequiredRef(_executionDirectory,
            $"refs/heads/{GoalWorktrees.BranchName(FollowerGoalId)}");
        if (!live.Equals(FollowerBranchHead, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Goal branch {GoalWorktrees.BranchName(FollowerGoalId)} changed while follower workspace was active.");
    }

    public FollowerGateReceipt ToReceipt(string followerPlanIdentity) =>
        new(LeaderGoalId, LeaderCandidateRevision, LeaderCandidateTree, BaseMainRevision,
            FollowerGoalId, FollowerBranchHead, TestedTreeRevision, followerPlanIdentity);

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            GoalWorktrees.RemoveMergeTrainWorkspace(_executionDirectory, Path);
            _disposed = true;
        }
        catch
        {
            GoalWorktrees.RecordAcceptanceCohortCleanupNeeded(Path, _cleanupHooks);
            throw;
        }
    }
}

public sealed record FollowerWorkspaceResult(
    FollowerGateWorkspace? Workspace, IReadOnlyList<string> ConflictPaths, string? ConflictDetail)
{
    public bool IsConflict => Workspace is null;
}

public static partial class GoalWorktrees
{
    public static FollowerWorkspaceResult CreateFollowerWorkspace(
        string executionDirectory, string observedMainRevision, GoalId leaderGoalId,
        string leaderCandidateRevision, GoalId followerGoalId, string followerBranchRevision,
        GoalWorktreeCleanupHooks? cleanupHooks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        ArgumentNullException.ThrowIfNull(leaderGoalId);
        ArgumentNullException.ThrowIfNull(followerGoalId);
        var root = Path.GetFullPath(executionDirectory);
        var main = AcceptanceCohortMemberBinding.NormalizeRevision(observedMainRevision, nameof(observedMainRevision));
        var candidate = AcceptanceCohortMemberBinding.NormalizeRevision(leaderCandidateRevision, nameof(leaderCandidateRevision));
        var follower = AcceptanceCohortMemberBinding.NormalizeRevision(followerBranchRevision, nameof(followerBranchRevision));
        var operationCleanupHooks = cleanupHooks ?? new GoalWorktreeCleanupHooks();
        if (!ResolveRequiredRef(root, "refs/heads/main").Equals(main, StringComparison.Ordinal))
            throw new InvalidOperationException("Follower main revision is stale before materialization.");
        if (GitCli.Run(root, "merge-base", "--is-ancestor", main, candidate).ExitCode != 0)
            throw new InvalidOperationException("Follower leader candidate does not descend from the observed main.");
        if (!ResolveRequiredRef(root, $"refs/heads/{BranchName(followerGoalId)}").Equals(follower, StringComparison.Ordinal))
            throw new InvalidOperationException("Follower branch revision is stale before materialization.");

        var candidateTree = ResolveRequiredRef(root, $"{candidate}^{{tree}}");
        var worktreeRoot = Path.Combine(root, DirectoryName);
        Directory.CreateDirectory(worktreeRoot);
        var workspacePath = Path.Combine(worktreeRoot, $"f-{Guid.NewGuid():N}"[..14]);
        var added = GitCli.Run(root, "worktree", "add", "--detach", workspacePath, candidate);
        if (added.ExitCode != 0)
            throw new InvalidOperationException($"Failed to create follower worktree: {added.Error}");

        try
        {
            if (HasMergeCommitsOutsideBase(root, candidate, follower) &&
                TryReadCleanMergeTree(root, candidate, follower, out var mergeTree))
            {
                var commit = GitCli.Run(workspacePath,
                    "-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
                    "commit-tree", mergeTree, "-p", candidate, "-m", $"Materialize follower {Prefix(followerGoalId)}");
                if (commit.ExitCode != 0)
                    throw new InvalidOperationException($"Failed to squash follower merge commits: {commit.Error}");
                var reset = GitCli.Run(workspacePath, "reset", "--hard", commit.Output.Trim());
                if (reset.ExitCode != 0)
                    throw new InvalidOperationException($"Failed to reset follower worktree: {reset.Error}");
            }
            else
            {
                // A revision, rather than a branch name, keeps the rebase detached.
                var rebase = GitCli.Run(workspacePath,
                    "-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
                    "rebase", "--merge", "--no-stat", "--onto", candidate, candidate, follower);
                if (rebase.ExitCode != 0)
                {
                    var conflicts = ReadMergeTrainConflictPaths(workspacePath);
                    _ = GitCli.Run(workspacePath, "rebase", "--abort");
                    if (conflicts.Count == 0)
                        throw new InvalidOperationException($"Follower rebase failed without conflict paths: {rebase.Error}");
                    RemoveMergeTrainWorkspace(root, workspacePath);
                    return new(null, conflicts, rebase.Error);
                }
            }

            SourceSizeRatchetRetightener.RetightenAndCommit(workspacePath, candidate, $"follower:{Prefix(followerGoalId)}");
            AcceptanceWorkspaceIntegrityPreparer.Prepare(workspacePath);
            var workspace = new FollowerGateWorkspace(root, workspacePath,
                ResolveRequiredRef(workspacePath, "HEAD"), ResolveRequiredRef(workspacePath, "HEAD^{tree}"),
                main, leaderGoalId, candidate, candidateTree, followerGoalId, follower, operationCleanupHooks);
            workspace.AssertFollowerBranchUnchanged();
            return new(workspace, [], null);
        }
        catch
        {
            try { RemoveMergeTrainWorkspace(root, workspacePath); }
            catch
            {
                RecordAcceptanceCohortCleanupNeeded(workspacePath, operationCleanupHooks);
                throw;
            }
            throw;
        }
    }
}
