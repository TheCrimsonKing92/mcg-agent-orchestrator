public sealed class CliAcceptanceGitRepositoryTemplateTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void CreateRepositories_WarmTemplate_LaunchesNoGitAndPreservesRepositoryShape()
    {
        var repositories = new List<string>();
        try
        {
            repositories.Add(CreateAcceptanceRepository());
            repositories.Add(CreateShortAcceptanceRepository());

            string regular;
            string shortRoot;
            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                regular = CreateAcceptanceRepository();
                repositories.Add(regular);
                Assert.Equal(0, launches.Count);
                shortRoot = CreateShortAcceptanceRepository();
                repositories.Add(shortRoot);
                Assert.Equal(0, launches.Count);
            }

            AssertRepositoryShape(regular);
            AssertRepositoryShape(shortRoot);
            Assert.Equal("mcg-short-tests", Directory.GetParent(shortRoot)!.Name);
            Assert.Matches("^[0-9a-f]{12}$", Path.GetFileName(shortRoot));
        }
        finally
        {
            foreach (var root in repositories)
                CleanupAcceptanceRepository(root, goalId: null);
        }
    }

    private static void AssertRepositoryShape(string root)
    {
        Assert.Equal("seed", File.ReadAllText(Path.Combine(root, "seed.txt")));
        Assert.Equal("# Dogfood Log" + Environment.NewLine, File.ReadAllText(Path.Combine(root, "DOGFOOD_LOG.md")));
        Assert.Equal("main", RunGitOutput(root, "branch", "--show-current").Trim());
        Assert.Equal("1", RunGitOutput(root, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal("Seed", RunGitOutput(root, "log", "-1", "--format=%s").Trim());
        Assert.Equal("", RunGitOutput(root, "status", "--porcelain").Trim());
        Assert.Equal("CLI Tests", RunGitOutput(root, "config", "--local", "user.name").Trim());
        Assert.Equal("tests@example.com", RunGitOutput(root, "config", "--local", "user.email").Trim());
    }
}
