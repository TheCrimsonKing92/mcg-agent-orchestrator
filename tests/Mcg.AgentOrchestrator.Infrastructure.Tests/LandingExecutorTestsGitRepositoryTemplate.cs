using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsGitRepositoryTemplate
{
    [Xunit.Fact]
    public void WarmedMaterializationCopiesInitialRepositoryWithoutGitLaunches()
    {
        var roots = new List<string>();
        try
        {
            roots.Add(LandingExecutorTests.CreateGitRepository());
            var originalAuthorDate = Environment.GetEnvironmentVariable("GIT_AUTHOR_DATE");
            try
            {
                using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
                for (var index = 0; index < 10; index++)
                {
                    // The legacy GitCli calls bypass the counter and inherit this variable.
                    // Fixed, differing dates make per-call commits produce different HEADs.
                    Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", $"2000-01-{index + 1:00}T00:00:00Z");
                    roots.Add(LandingExecutorTests.CreateGitRepository());
                }

                Assert.Equal(0, launches.Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_AUTHOR_DATE", originalAuthorDate);
            }

            Assert.Equal(11, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var initialHeads = new List<string>();
            foreach (var root in roots)
            {
                Assert.True(Directory.Exists(root));
                Assert.Equal(Path.Combine(Path.GetTempPath(), "mcg-landing-tests"), Path.GetDirectoryName(root));
                Assert.True(Guid.TryParseExact(Path.GetFileName(root), "N", out _));
                Assert.Equal("main", QueryGit(root, "rev-parse", "--abbrev-ref", "HEAD"));
                Assert.Equal("Initial", QueryGit(root, "log", "--format=%s"));
                Assert.Equal("tests@example.invalid", QueryGit(root, "config", "user.email"));
                Assert.Equal("Tests", QueryGit(root, "config", "user.name"));
                var exclude = File.ReadAllLines(Path.Combine(root, ".git", "info", "exclude"));
                Assert.Contains(".orchestrator-test-remotes/", exclude);
                Assert.Contains(".orchestrator/", exclude);
                Assert.Equal("initial" + Environment.NewLine, File.ReadAllText(Path.Combine(root, "README.md")));
                Assert.True(File.Exists(OrchestratorWorkspace.ForDirectory(root).SqliteStatePath));
                Assert.Equal("", QueryGit(root, "status", "--porcelain"));
                initialHeads.Add(QueryGit(root, "rev-parse", "HEAD"));
            }

            Assert.Single(initialHeads.Distinct(StringComparer.Ordinal));
        }
        finally
        {
            foreach (var root in roots)
                LandingExecutorTests.TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CommitInMaterializedRootLeavesOtherAndFutureRootsUnchanged()
    {
        var roots = new List<string>();
        try
        {
            roots.Add(LandingExecutorTests.CreateGitRepository());
            roots.Add(LandingExecutorTests.CreateGitRepository());
            var initialHead = QueryGit(roots[1], "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(roots[0], "independent.txt"), "only in first root");
            InfrastructureTestSupport.RunGitWithCommitPostcondition(roots[0], ["add", "independent.txt"]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(roots[0], ["commit", "-m", "Independent"]);

            Assert.NotEqual(initialHead, QueryGit(roots[0], "rev-parse", "HEAD"));
            roots.Add(LandingExecutorTests.CreateGitRepository());
            foreach (var root in roots.Skip(1))
            {
                Assert.False(File.Exists(Path.Combine(root, "independent.txt")));
                Assert.Equal("", QueryGit(root, "status", "--porcelain"));
                Assert.Equal(initialHead, QueryGit(root, "rev-parse", "HEAD"));
                Assert.Equal("Initial", QueryGit(root, "log", "--format=%s"));
            }
        }
        finally
        {
            foreach (var root in roots)
                LandingExecutorTests.TryDeleteDirectory(root);
        }
    }

    private static string QueryGit(string root, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }
}
