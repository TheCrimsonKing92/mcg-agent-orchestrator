using Mcg.AgentOrchestrator.Infrastructure;

internal static class PastDatedSeedRepository
{
    internal static readonly DateTimeOffset SeedCommitTime =
        new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static string Create()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"mcg-cob-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repo);
            RunGit(repo, ["init"]);
            RunGit(repo, ["config", "user.email", "tests@example.com"]);
            RunGit(repo, ["config", "user.name", "Worktree Tests"]);
            File.AppendAllText(
                Path.Combine(repo, ".git", "info", "exclude"),
                ".orchestrator/" + Environment.NewLine);
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, ["add", "-A"]);
            RunGit(repo, ["commit", "-m", "Seed"], new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = SeedCommitTime.ToString("O"),
                ["GIT_COMMITTER_DATE"] = SeedCommitTime.ToString("O")
            });
            return repo;
        }
        catch
        {
            Delete(repo);
            throw;
        }
    }

    internal static void Delete(string repo) => TempRootJanitor.DeleteTree(repo);

    private static void RunGit(
        string repo,
        string[] arguments,
        IReadOnlyDictionary<string, string>? commandEnvironment = null)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repo, arguments, commandEnvironment);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{result.Command} failed: exit={result.ExitCode}; classification={result.Classification}; " +
                $"stdout: {result.StandardOutput}; stderr: {result.StandardError}");
        }
    }
}
