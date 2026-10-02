namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal const string OwnerResultsCreatorFileName = ".owner-results-creator";

    private static void CreateOwnerResultsDirectory(string directory)
    {
        var alreadyExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (alreadyExisted)
        {
            return;
        }

        // A new top-level owner entry can be attributed to its creating process by
        // the test-assembly guard, even when other test assemblies run concurrently.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var marker = new FileStream(
            Path.Combine(directory, OwnerResultsCreatorFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        using var writer = new StreamWriter(marker);
        writer.Write($"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}");
    }

    internal static bool WasOwnerResultsDirectoryCreatedByCurrentProcess(string directory)
    {
        var markerPath = Path.Combine(directory, OwnerResultsCreatorFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return File.ReadAllText(markerPath).Equals(
            $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}",
            StringComparison.Ordinal);
    }

    internal static string ResolveOwnerResultsRootForCandidate(string worktreePath)
    {
        var configuredPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        var configuredRoot = string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : ResolveOwnerResultsRepositoryRoot(configuredPath);
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            return configuredRoot ?? Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName);
        }

        var candidateRoot = ResolveOwnerResultsRepositoryRoot(worktreePath);
        if (!candidateRoot.Equals(
                Path.Combine(Path.GetTempPath(), OwnerResultsRootDirectoryName),
                StringComparison.OrdinalIgnoreCase))
        {
            return candidateRoot;
        }

        // A linked worktree outside .orchestrator-worktrees can still belong to the
        // configured repository. Keep its existing results location in that case.
        if (configuredRoot is not null &&
            TryResolveLinkedWorktreeMainRoot(worktreePath, out var linkedRoot) &&
            linkedRoot.Equals(configuredRoot, StringComparison.OrdinalIgnoreCase))
        {
            return configuredRoot;
        }

        return candidateRoot;
    }

    private static bool TryResolveLinkedWorktreeMainRoot(string worktreePath, out string mainRoot)
    {
        mainRoot = string.Empty;
        var candidate = new DirectoryInfo(Path.GetFullPath(worktreePath));
        while (candidate is not null)
        {
            var gitFile = Path.Combine(candidate.FullName, ".git");
            if (File.Exists(gitFile))
            {
                try
                {
                    var line = File.ReadLines(gitFile).FirstOrDefault();
                    if (line is null || !line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var gitDirectory = Path.GetFullPath(line[7..].Trim(), candidate.FullName);
                    var worktreesDirectory = Directory.GetParent(gitDirectory);
                    if (worktreesDirectory?.Name.Equals("worktrees", StringComparison.OrdinalIgnoreCase) != true ||
                        worktreesDirectory.Parent?.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        return false;
                    }

                    mainRoot = worktreesDirectory.Parent.Parent?.FullName ?? string.Empty;
                    return mainRoot.Length > 0;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            candidate = candidate.Parent;
        }

        return false;
    }
}
