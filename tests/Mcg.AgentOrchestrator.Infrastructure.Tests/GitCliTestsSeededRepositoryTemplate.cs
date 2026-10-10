// Parallel-safe: the lazy template is read-only after creation; each fact mutates only its own GUID copy.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class GitCliTestsSeededRepositoryTemplate
{
    [Xunit.Fact]
    public void CreateCopy_WarmTemplate_LaunchesNoGitAndPreservesSeed()
    {
        var first = SeededGitRepositoryTemplate.CreateCopy();
        string? copy = null;
        try
        {
            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                copy = SeededGitRepositoryTemplate.CreateCopy();
                Assert.Equal(0, launches.Count);
            }

            Assert.NotEqual(first, copy);
            Assert.True(Directory.Exists(Path.Combine(copy, ".git")));
            Assert.Equal("seed", File.ReadAllText(Path.Combine(copy, "seed.txt")));
            Assert.Equal(RunGit(SeededGitRepositoryTemplate.TemplatePath, "rev-parse", "HEAD"),
                RunGit(copy, "rev-parse", "HEAD"));
            Assert.Equal("1", RunGit(copy, "rev-list", "--count", "HEAD"));
            Assert.Equal("Seed", RunGit(copy, "log", "-1", "--format=%s"));
            Assert.Equal("", RunGit(copy, "status", "--porcelain"));
            Assert.Equal("GitCli Tests", RunGit(copy, "config", "--local", "user.name"));
            Assert.Equal("tests@example.com", RunGit(copy, "config", "--local", "user.email"));
        }
        finally
        {
            DeleteDirectory(first);
            if (copy is not null) DeleteDirectory(copy);
        }
    }

    [Xunit.Fact]
    public void CreateCopy_AfterAnotherCopyChanges_PreservesSeed()
    {
        var changed = SeededGitRepositoryTemplate.CreateCopy();
        string? later = null;
        try
        {
            var seedHead = RunGit(changed, "rev-parse", "HEAD");
            RunGit(changed, "config", "user.name", "Changed Copy");
            RunGit(changed, "config", "user.email", "changed@example.com");
            File.WriteAllText(Path.Combine(changed, "seed.txt"), "changed");
            RunGit(changed, "add", "-A");
            RunGit(changed, "commit", "-m", "Copy change");
            Assert.Equal("2", RunGit(changed, "rev-list", "--count", "HEAD"));
            RunGit(changed, "checkout", "--detach");

            later = SeededGitRepositoryTemplate.CreateCopy();

            Assert.NotEqual(changed, later);
            Assert.Equal(seedHead, RunGit(later, "rev-parse", "HEAD"));
            Assert.Equal("1", RunGit(later, "rev-list", "--count", "HEAD"));
            Assert.Equal("Seed", RunGit(later, "log", "-1", "--format=%s"));
            Assert.Equal("seed", File.ReadAllText(Path.Combine(later, "seed.txt")));
            Assert.Equal("", RunGit(later, "status", "--porcelain"));
            Assert.Equal("GitCli Tests", RunGit(later, "config", "--local", "user.name"));
            Assert.Equal("tests@example.com", RunGit(later, "config", "--local", "user.email"));
        }
        finally
        {
            DeleteDirectory(changed);
            if (later is not null) DeleteDirectory(later);
        }
    }

    [Xunit.Fact]
    public void CreateSeededRepository_WarmTemplate_LaunchesNoGit()
    {
        _ = SeededGitRepositoryTemplate.TemplatePath;
        string? copy = null;
        try
        {
            using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

            copy = GitCliTests.CreateSeededRepository();

            Assert.Equal(0, launches.Count);
        }
        finally
        {
            if (copy is not null) DeleteDirectory(copy);
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
