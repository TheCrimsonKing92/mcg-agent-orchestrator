namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalWorktreeGitMetadataAccess(
    string WorktreePath,
    string IndexLockPath,
    string CurrentIdentity,
    bool CurrentProcessCanWriteIndexLock,
    bool WorkerCanWriteIndexLock,
    string WorkerWriteDisposition,
    string CommitContract,
    string? Error);

public static class GoalWorktreeGitMetadata
{
    public static GoalWorktreeGitMetadataAccess Inspect(
        string worktreePath,
        WorkerSandboxOptions? sandboxOptions = null)
    {
        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var indexLockPath = ResolveGitPath(fullWorktreePath, "index.lock");
        var currentIdentity = Environment.UserName;
        var currentCanWrite = false;
        string? error = null;

        if (string.IsNullOrWhiteSpace(indexLockPath))
        {
            indexLockPath = Path.Combine(fullWorktreePath, ".git", "index.lock");
            error = "git rev-parse --git-path index.lock failed";
        }
        else
        {
            currentCanWrite = TryProbeCreateFile(indexLockPath, out error);
        }

        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
        var lowIntegrityWorker = sandbox.Enabled && OperatingSystem.IsWindows();
        var workerCanWrite = !lowIntegrityWorker && currentCanWrite;
        var disposition = lowIntegrityWorker
            ? "blocked-by-low-integrity"
            : currentCanWrite ? "same-as-orchestrator" : "unavailable";

        return new GoalWorktreeGitMetadataAccess(
            fullWorktreePath,
            indexLockPath,
            currentIdentity,
            currentCanWrite,
            workerCanWrite,
            disposition,
            "workers edit worktree files; orchestrator commits verified dirty edits on behalf",
            error);
    }

    private static string ResolveGitPath(string workingDirectory, string path)
    {
        var result = GitCli.Run(workingDirectory, "rev-parse", "--git-path", path);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output))
        {
            return string.Empty;
        }

        var resolved = result.Output.Trim();
        return Path.IsPathFullyQualified(resolved)
            ? Path.GetFullPath(resolved)
            : Path.GetFullPath(Path.Combine(workingDirectory, resolved));
    }

    private static bool TryProbeCreateFile(string path, out string? error)
    {
        error = null;
        if (File.Exists(path))
        {
            error = "index.lock already exists";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
