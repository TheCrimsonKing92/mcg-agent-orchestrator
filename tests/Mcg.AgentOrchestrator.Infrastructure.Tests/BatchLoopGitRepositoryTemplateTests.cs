public sealed class BatchLoopGitRepositoryTemplateTests : ConductorBatchLoopTests
{
    public BatchLoopGitRepositoryTemplateTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void CopyTo_WarmTemplate_LaunchesNoGitAndPreservesRepositoryShape()
    {
        var repositories = new List<string>();
        try
        {
            repositories.Add(CreateSeededGitRepository());
            var copy = CreateTempDirectory("mcg-batch-loop");
            repositories.Add(copy);

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                BatchLoopGitRepositoryTemplate.CopyTo(copy);
                Assert.Equal(0, launches.Count);
            }

            // The helper uses GitCli, which the probe counter does not observe.
            var helperCopy = CreateSeededGitRepository();
            repositories.Add(helperCopy);
            AssertRepositoryShape(copy);
            AssertRepositoryShape(helperCopy);
        }
        finally
        {
            foreach (var root in repositories)
                TryDeleteDirectory(root);
        }
    }

    private static void AssertRepositoryShape(string root)
    {
        Assert.Equal("seed", File.ReadAllText(Path.Combine(root, "seed.txt")));
        Assert.Equal("main", ProbeGit(root, "branch", "--show-current"));
        Assert.Equal("1", ProbeGit(root, "rev-list", "--count", "HEAD"));
        Assert.Equal("Seed", ProbeGit(root, "log", "-1", "--format=%s"));
        Assert.Equal("", ProbeGit(root, "status", "--porcelain"));
        Assert.Equal("Batch Loop Tests", ProbeGit(root, "config", "--local", "user.name"));
        Assert.Equal("tests@example.com", ProbeGit(root, "config", "--local", "user.email"));
    }

    private static string ProbeGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }
}
