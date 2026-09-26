namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal static string ResolveOwnerResultsRootForCandidate(string worktreePath)
    {
        var configuredPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        var configuredRoot = string.IsNullOrWhiteSpace(configuredPath)
            ? null
            : ResolveOwnerResultsRepositoryRoot(configuredPath);
        if (string.IsNullOrWhiteSpace(worktreePath))
        {
            return configuredRoot ?? Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-results");
        }

        var candidateRoot = ResolveOwnerResultsRepositoryRoot(worktreePath);
        if (!candidateRoot.Equals(
                Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-results"),
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
