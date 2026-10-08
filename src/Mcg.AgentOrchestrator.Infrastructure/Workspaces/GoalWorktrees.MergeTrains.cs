using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class MergeTrainWorkspace : IDisposable
{
    private readonly string _executionDirectory;
    private readonly GoalWorktreeCleanupHooks _cleanupHooks;
    private bool _disposed;

    internal MergeTrainWorkspace(
        string executionDirectory,
        string path,
        string commitRevision,
        string treeRevision,
        IReadOnlyList<MergeTrainMemberBinding> members,
        IReadOnlyList<MergeTrainEjection> ejections,
        IReadOnlyDictionary<GoalId, string> originalBranchRevisions,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        _executionDirectory = executionDirectory;
        _cleanupHooks = cleanupHooks ?? throw new ArgumentNullException(nameof(cleanupHooks));
        Path = path;
        CommitRevision = commitRevision;
        TreeRevision = treeRevision;
        Members = members;
        Ejections = ejections;
        OriginalBranchRevisions = originalBranchRevisions;
    }

    public string Path { get; }
    public string CommitRevision { get; }
    public string TreeRevision { get; }
    public IReadOnlyList<MergeTrainMemberBinding> Members { get; }
    public IReadOnlyList<MergeTrainEjection> Ejections { get; }
    public IReadOnlyDictionary<GoalId, string> OriginalBranchRevisions { get; }

    public void AssertGoalBranchesUnchanged()
    {
        foreach (var branch in OriginalBranchRevisions)
        {
            var current = GoalWorktrees.ResolveRequiredRef(
                _executionDirectory,
                $"refs/heads/{GoalWorktrees.BranchName(branch.Key)}");
            if (!current.Equals(branch.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Goal branch {GoalWorktrees.BranchName(branch.Key)} changed while merge train workspace was active.");
            }
        }
    }

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

public static partial class GoalWorktrees
{
    public static MergeTrainWorkspace CreateMergeTrainWorkspace(
        string executionDirectory,
        string observedMainRevision,
        IReadOnlyList<MergeTrainMemberBinding> members,
        GoalWorktreeCleanupHooks? cleanupHooks = null,
        DateTimeOffset? committerDate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        ArgumentNullException.ThrowIfNull(members);
        var operationCleanupHooks = cleanupHooks ?? new GoalWorktreeCleanupHooks();
        if (members.Count is < 2 or > 3 || members.Select(member => member.GoalId).Distinct().Count() != members.Count)
        {
            throw new ArgumentException("A disposable merge train requires two or three distinct members.", nameof(members));
        }

        var root = Path.GetFullPath(executionDirectory);
        var main = MergeTrainMemberBinding.NormalizeRevision(observedMainRevision, nameof(observedMainRevision));
        if (!ResolveRequiredRef(root, "refs/heads/main").Equals(main, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Merge train main revision is stale before materialization.");
        }

        var originalBranches = members.ToDictionary(
            member => member.GoalId,
            member => ResolveRequiredRef(root, $"refs/heads/{BranchName(member.GoalId)}"));
        foreach (var member in members)
        {
            var live = originalBranches[member.GoalId];
            if (!live.Equals(member.BranchRevision, StringComparison.Ordinal) ||
                !live.Equals(member.CandidateRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Merge train member {member.GoalId.Value[..8]} is stale before materialization.");
            }
        }

        var worktreeRoot = Path.Combine(root, DirectoryName);
        Directory.CreateDirectory(worktreeRoot);
        var workspacePath = Path.Combine(worktreeRoot, $"t-{Guid.NewGuid():N}"[..14]);
        var added = GitCli.Run(root, "worktree", "add", "--detach", workspacePath, main);
        if (added.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create merge train worktree: {added.Error}");
        }

        try
        {
            var materialized = new List<MergeTrainMemberBinding>(members.Count);
            var ejections = new List<MergeTrainEjection>();
            foreach (var member in members)
            {
                var step = StreamComposer.RebaseOntoPriorHead(root, workspacePath, main,
                    member.CandidateRevision, member.GoalId, "Merge train",
                    args => committerDate is null
                        ? GitCli.Run(workspacePath, args)
                        : GitCli.RunWithEnvironment(workspacePath,
                            new Dictionary<string, string>
                            {
                                ["GIT_COMMITTER_DATE"] = FormattableString.Invariant($"@{committerDate.Value.ToUnixTimeSeconds()} +0000")
                            }, args));
                if (step.Ejection is not null)
                {
                    ejections.Add(step.Ejection);
                    continue;
                }
                materialized.Add(member.WithRebasedHead(step.Head!));
            }

            SourceSizeRatchetRetightener.RetightenAndCommit(workspacePath, main,
                $"train:{string.Join('+', materialized.Select(member => Prefix(member.GoalId)))}", committerDate);
            AcceptanceWorkspaceIntegrityPreparer.Prepare(workspacePath);

            var result = new MergeTrainWorkspace(
                root,
                workspacePath,
                ResolveRequiredRef(workspacePath, "HEAD"),
                ResolveRequiredRef(workspacePath, "HEAD^{tree}"),
                Array.AsReadOnly(materialized.ToArray()),
                Array.AsReadOnly(ejections.ToArray()),
                originalBranches,
                operationCleanupHooks);
            result.AssertGoalBranchesUnchanged();
            return result;
        }
        catch
        {
            RemoveMergeTrainWorkspace(root, workspacePath);
            throw;
        }
    }

    internal static void RemoveMergeTrainWorkspace(string executionDirectory, string workspacePath)
    {
        var remove = GitCli.Run(executionDirectory, "worktree", "remove", "--force", workspacePath);
        if (remove.ExitCode != 0 && Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException($"Failed to remove merge train worktree '{workspacePath}': {remove.Error}");
        }
        _ = GitCli.Run(executionDirectory, "worktree", "prune");
    }

    internal static IReadOnlyList<string> ReadMergeTrainConflictPaths(string workspacePath)
    {
        var result = GitCli.Run(workspacePath, "diff", "--name-only", "--diff-filter=U");
        return result.ExitCode == 0
            ? result.Output.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }
}
