using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static GoalWorktreeGitMetadataAccess InspectGitMetadataAccess(string worktreePath, WorkerSandboxOptions? sandboxOptions = null) => GoalWorktreeGitMetadata.Inspect(worktreePath, sandboxOptions);

    public static string Ensure(
        string executionDirectory,
        GoalId goalId,
        GoalWorktreeCleanupHooks? cleanupHooks = null)
    {
        cleanupHooks ??= new GoalWorktreeCleanupHooks();
        var existing = TryResolve(executionDirectory, goalId);
        if (existing is not null)
        {
            FastForwardToBaseIfStale(executionDirectory, existing);
            return existing;
        }

        RequireGitWorkTree(executionDirectory);
        EnsureWorktreeRootIgnored(executionDirectory);

        var path = WorktreePath(executionDirectory, goalId);
        var branch = BranchName(goalId);
        var branchExists = BranchExists(executionDirectory, branch);
        var result = AddWorktree(executionDirectory, path, branch, branchExists);
        if (result.ExitCode != 0 && WorktreeAddFailedBecausePathExists(result, path))
        {
            ClearOrphanDirectory(path, kernel: null, operation: "worktree-add-retry", cleanupHooks);
            result = AddWorktree(executionDirectory, path, branch, BranchExists(executionDirectory, branch));
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create goal workspace at '{path}': {result.Error}");
        }

        FastForwardToBaseIfStale(executionDirectory, path);
        return path;
    }

    // Brings an UNDRIVEN goal worktree up to the base branch (main) before a worker runs, so workers
    // never build on a stale base — building on an old main and then failing to merge over fixes that
    // landed meanwhile is the stale-base conflict that otherwise forces a manual re-dispatch. Only
    // fast-forwards: a branch that has diverged (its own commits ahead of base) or a dirty worktree is
    // left untouched, because that is real in-progress work reconciled by TryRebaseOntoMain/acceptance,
    // not here. Best-effort: never blocks dispatch.
    private static void FastForwardToBaseIfStale(string executionDirectory, string worktreePath)
    {
        try
        {
            if (GitCli.IsWorktreeDirty(worktreePath))
            {
                return;
            }

            var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
            // --ff-only fast-forwards when the branch is strictly behind base, is a no-op when already
            // up to date, and fails harmlessly (branch left as-is) when the branch has diverged with
            // its own commits — exactly the "only advance undriven branches" semantics we want.
            GitCli.Run(worktreePath, "merge", "--ff-only", baseBranch);
        }
        catch
        {
            // Freshness is best-effort; a worker building on a slightly stale base still goes through
            // the acceptance gate, which catches a genuine conflict.
        }
    }


    private static GitCli.GitResult AddWorktree(string executionDirectory, string path, string branch, bool branchExists)
    {
        return branchExists
            ? GitCli.Run(executionDirectory, "worktree", "add", path, branch)
            : GitCli.Run(executionDirectory, "worktree", "add", path, "-b", branch);
    }

    private static bool WorktreeAddFailedBecausePathExists(GitCli.GitResult result, string path)
    {
        if (result.ExitCode == 0 || !Directory.Exists(path))
        {
            return false;
        }

        var message = $"{result.Output}{Environment.NewLine}{result.Error}";
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }


    private static void EnsureWorktreeRootIgnored(string executionDirectory)
    {
        try
        {
            var gitDirResult = GitCli.Run(executionDirectory, "rev-parse", "--git-dir");
            if (gitDirResult.ExitCode != 0 || string.IsNullOrWhiteSpace(gitDirResult.Output))
            {
                return;
            }

            var gitDir = gitDirResult.Output.Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(executionDirectory, gitDir));
            }

            var infoDirectory = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDirectory);
            var excludePath = Path.Combine(infoDirectory, "exclude");
            const string ignoreEntry = ".orchestrator-worktrees/";
            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            if (existing
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => string.Equals(line, ignoreEntry, StringComparison.Ordinal)))
            {
                return;
            }

            File.AppendAllText(excludePath,
                (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : string.Empty) +
                ignoreEntry +
                Environment.NewLine);
        }
        catch
        {
            // Ignore hygiene failures; git worktree creation remains the authoritative operation.
        }
    }
}
