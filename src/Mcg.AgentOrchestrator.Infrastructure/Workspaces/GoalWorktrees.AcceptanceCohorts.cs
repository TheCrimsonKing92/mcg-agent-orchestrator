using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum AcceptanceCohortMaterializationFailureKind
{
    StaleBinding,
    MergeConflict,
    WorkspaceFailure,
    ManifestUnavailable
}

public sealed class AcceptanceCohortMaterializationException : InvalidOperationException
{
    public AcceptanceCohortMaterializationException(
        AcceptanceCohortMaterializationFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AcceptanceCohortMaterializationFailureKind Kind { get; }
}

public sealed class AcceptanceCohortWorkspace : IDisposable
{
    private readonly string _executionDirectory;
    private bool _disposed;
    internal static Action<string, string> WorkspaceRemover { get; set; } =
        GoalWorktrees.RemoveAcceptanceCohortWorkspace;

    internal AcceptanceCohortWorkspace(
        string executionDirectory,
        string path,
        string commitRevision,
        string treeRevision,
        IReadOnlyDictionary<GoalId, string> originalBranchRevisions)
    {
        _executionDirectory = executionDirectory;
        Path = path;
        CommitRevision = commitRevision;
        TreeRevision = treeRevision;
        OriginalBranchRevisions = originalBranchRevisions;
    }

    public string Path { get; }
    public string CommitRevision { get; }
    public string TreeRevision { get; }
    public IReadOnlyDictionary<GoalId, string> OriginalBranchRevisions { get; }

    public void AssertGoalBranchesUnchanged()
    {
        foreach (var pair in OriginalBranchRevisions)
        {
            var current = GoalWorktrees.ResolveRequiredRef(
                _executionDirectory,
                $"refs/heads/{GoalWorktrees.BranchName(pair.Key)}");
            if (!current.Equals(pair.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Goal branch {GoalWorktrees.BranchName(pair.Key)} changed while cohort workspace was active.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            WorkspaceRemover(_executionDirectory, Path);
            _disposed = true;
        }
        catch
        {
            GoalWorktrees.RecordAcceptanceCohortCleanupNeeded(Path);
            throw;
        }
    }
}

public static partial class GoalWorktrees
{
    public static AcceptanceCohortWorkspace CreateAcceptancePartitionWorkspace(
        string executionDirectory,
        string observedMainRevision,
        AcceptanceCohortMemberBinding member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        ArgumentNullException.ThrowIfNull(member);
        var root = System.IO.Path.GetFullPath(executionDirectory);
        var normalizedMain = AcceptanceCohortMemberBinding.NormalizeRevision(
            observedMainRevision,
            nameof(observedMainRevision));
        var liveMain = ResolveRequiredRef(root, "refs/heads/main");
        if (!liveMain.Equals(normalizedMain, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attribution main revision changed before partition materialization.");
        }
        var branch = ResolveRequiredRef(root, $"refs/heads/{BranchName(member.GoalId)}");
        if (!branch.Equals(member.BranchRevision, StringComparison.Ordinal) ||
            !branch.Equals(member.CandidateRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Attribution member {member.GoalId.Value[..8]} changed before partition materialization.");
        }

        var worktreeRoot = System.IO.Path.Combine(root, DirectoryName);
        Directory.CreateDirectory(worktreeRoot);
        var workspacePath = System.IO.Path.Combine(
            worktreeRoot,
            $"cohort-partition-{Guid.NewGuid():N}");
        try
        {
            var added = GitCli.Run(root, "worktree", "add", "--detach", workspacePath, normalizedMain);
            if (added.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create attribution worktree: {added.Error}");
            }
            var merge = GitCli.Run(
                workspacePath,
                "-c", "user.name=mcg-orchestrator",
                "-c", "user.email=mcg-orchestrator@localhost",
                "merge", "--no-ff", "--no-edit", member.CandidateRevision);
            if (merge.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to materialize attribution member {member.GoalId.Value[..8]}: {merge.Error}");
            }
            var result = new AcceptanceCohortWorkspace(
                root,
                workspacePath,
                ResolveRequiredRef(workspacePath, "HEAD"),
                ResolveRequiredRef(workspacePath, "HEAD^{tree}"),
                new Dictionary<GoalId, string> { [member.GoalId] = branch });
            result.AssertGoalBranchesUnchanged();
            return result;
        }
        catch (Exception materializationFailure)
        {
            RemoveFailedMaterializationWorkspace(
                root,
                workspacePath,
                "Acceptance partition",
                materializationFailure);
            throw;
        }
    }

    public static AcceptanceCohortWorkspace CreateAcceptanceCohortWorkspace(
        string executionDirectory,
        string observedMainRevision,
        IReadOnlyList<AcceptanceCohortMemberBinding> members)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count != 2 || members[0].GoalId == members[1].GoalId)
        {
            throw new ArgumentException("A disposable acceptance cohort requires exactly two distinct members.", nameof(members));
        }

        var root = System.IO.Path.GetFullPath(executionDirectory);
        var normalizedMain = AcceptanceCohortMemberBinding.NormalizeRevision(
            observedMainRevision,
            nameof(observedMainRevision));
        var liveMain = ResolveRequiredRef(root, "refs/heads/main");
        if (!liveMain.Equals(normalizedMain, StringComparison.Ordinal))
        {
            throw new AcceptanceCohortMaterializationException(
                AcceptanceCohortMaterializationFailureKind.StaleBinding,
                $"Cohort main revision is stale: bound={normalizedMain}, live={liveMain}.");
        }

        var branchRevisions = new Dictionary<GoalId, string>();
        foreach (var member in members)
        {
            var branch = ResolveRequiredRef(root, $"refs/heads/{BranchName(member.GoalId)}");
            if (!branch.Equals(member.BranchRevision, StringComparison.Ordinal) ||
                !branch.Equals(member.CandidateRevision, StringComparison.Ordinal))
            {
                throw new AcceptanceCohortMaterializationException(
                    AcceptanceCohortMaterializationFailureKind.StaleBinding,
                    $"Cohort member {member.GoalId.Value[..8]} is stale before materialization.");
            }
            branchRevisions.Add(member.GoalId, branch);
        }

        var worktreeRoot = System.IO.Path.Combine(root, DirectoryName);
        Directory.CreateDirectory(worktreeRoot);
        var workspacePath = System.IO.Path.Combine(
            worktreeRoot,
            $"cohort-{Guid.NewGuid():N}");
        try
        {
            var added = GitCli.Run(root, "worktree", "add", "--detach", workspacePath, normalizedMain);
            if (added.ExitCode != 0)
            {
                throw new AcceptanceCohortMaterializationException(
                    AcceptanceCohortMaterializationFailureKind.WorkspaceFailure,
                    $"Failed to create cohort worktree: {added.Error}");
            }

            foreach (var member in members)
            {
                var merge = GitCli.Run(
                    workspacePath,
                    "-c", "user.name=mcg-orchestrator",
                    "-c", "user.email=mcg-orchestrator@localhost",
                    "merge", "--no-ff", "--no-edit", member.CandidateRevision);
                if (merge.ExitCode != 0)
                {
                    throw new AcceptanceCohortMaterializationException(
                        AcceptanceCohortMaterializationFailureKind.MergeConflict,
                        $"Failed to materialize cohort member {member.GoalId.Value[..8]}: {merge.Error}");
                }
            }

            var commit = ResolveRequiredRef(workspacePath, "HEAD");
            var tree = ResolveRequiredRef(workspacePath, "HEAD^{tree}");
            var result = new AcceptanceCohortWorkspace(
                root,
                workspacePath,
                commit,
                tree,
                branchRevisions);
            result.AssertGoalBranchesUnchanged();
            return result;
        }
        catch (Exception materializationFailure)
        {
            RemoveFailedMaterializationWorkspace(
                root,
                workspacePath,
                "Cohort",
                materializationFailure);
            throw;
        }
    }

    public static string ComputeAcceptanceManifestIdentity(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var manifestPath = System.IO.Path.Combine(workspacePath, "config", "acceptance-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"Acceptance manifest is missing from cohort workspace: {manifestPath}");
        }
        return $"manifest-sha256-{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)))}";
    }

    internal static string ResolveRequiredRef(string workingDirectory, string reference)
    {
        var result = GitCli.Run(workingDirectory, "rev-parse", "--verify", reference);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            throw new InvalidOperationException($"Could not resolve Git ref '{reference}': {result.Error}");
        }
        return AcceptanceCohortMemberBinding.NormalizeRevision(result.Output, nameof(reference));
    }

    internal static void RemoveAcceptanceCohortWorkspace(string executionDirectory, string workspacePath)
    {
        var remove = GitCli.Run(executionDirectory, "worktree", "remove", "--force", workspacePath);
        if (remove.ExitCode != 0 && Directory.Exists(workspacePath))
        {
            throw new InvalidOperationException($"Failed to remove cohort worktree '{workspacePath}': {remove.Error}");
        }
        _ = GitCli.Run(executionDirectory, "worktree", "prune");
    }

    internal static void RecordAcceptanceCohortCleanupNeeded(string workspacePath) =>
        RecordCleanupNeeded(workspacePath, "cohort:worktree-remove-failed", hooks: GoalWorktreeCleanupHooks.Default);

    private static void RemoveFailedMaterializationWorkspace(
        string executionDirectory,
        string workspacePath,
        string workspaceKind,
        Exception materializationFailure)
    {
        try
        {
            AcceptanceCohortWorkspace.WorkspaceRemover(executionDirectory, workspacePath);
        }
        catch (Exception cleanupFailure)
        {
            RecordAcceptanceCohortCleanupNeeded(workspacePath);
            throw new AcceptanceCohortMaterializationException(
                AcceptanceCohortMaterializationFailureKind.WorkspaceFailure,
                $"{workspaceKind} materialization failed and its disposable workspace could not be removed: {cleanupFailure.Message}",
                new AggregateException(materializationFailure, cleanupFailure));
        }
    }
}
