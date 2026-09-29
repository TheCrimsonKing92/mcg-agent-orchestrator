[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class DashboardTestGitIsolationEnvMutationTests
{
    [Xunit.Fact]
    public void GitIgnoresAmbientRepositoryAndGlobalConfiguration()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var repoA = Path.Combine(root, "repo-a");
        var repoB = Path.Combine(root, "repo-b");
        Directory.CreateDirectory(repoA);
        Directory.CreateDirectory(repoB);
        try
        {
            Seed(repoA, "repo-a-seed");
            Seed(repoB, "repo-b-seed");
            var ambientConfig = Path.Combine(root, "ambient.gitconfig");
            var distinctive = $"ambient-{Guid.NewGuid():N}";
            File.WriteAllText(ambientConfig, $"[mcg]\n\tisolationProbe = {distinctive}\n");
            var oldDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
            var oldConfig = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
            try
            {
                Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(repoB, ".git"));
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", ambientConfig);
                Xunit.Assert.Equal("repo-a-seed", DashboardTestGit.Run(repoA, "log", "-1", "--format=%s").Trim());
                Xunit.Assert.Equal(Path.GetFullPath(Path.Combine(repoA, ".git")),
                    Path.GetFullPath(DashboardTestGit.Run(repoA, "rev-parse", "--absolute-git-dir").Trim()),
                    ignoreCase: OperatingSystem.IsWindows());
                Xunit.Assert.DoesNotContain(distinctive, DashboardTestGit.Run(repoA, "config", "--list"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_DIR", oldDirectory);
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldConfig);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Seed(string directory, string subject)
    {
        DashboardTestGit.Run(directory, "init", "-b", "main");
        DashboardTestGit.Run(directory, "config", "user.name", "Fixture");
        DashboardTestGit.Run(directory, "config", "user.email", "fixture@example.test");
        File.WriteAllText(Path.Combine(directory, "seed.txt"), subject);
        DashboardTestGit.Run(directory, "add", "seed.txt");
        DashboardTestGit.Run(directory, "commit", "-m", subject);
    }
}
