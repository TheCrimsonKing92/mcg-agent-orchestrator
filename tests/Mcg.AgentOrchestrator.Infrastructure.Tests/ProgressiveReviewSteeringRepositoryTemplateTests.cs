public sealed class ProgressiveReviewSteeringRepositoryTemplateTests
{
    // Copies use unique roots; the launch counter is scoped to this execution flow.
    [Xunit.Fact]
    public void CopyTo_WarmTemplate_LaunchesNoGitAndPreservesRepositoryShape()
    {
        var warm = CreateTempPath();
        var copy = CreateTempPath();
        var reference = CreateTempPath();
        try
        {
            ProgressiveReviewSteeringRepositoryTemplate.CopyTo(warm);
            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                ProgressiveReviewSteeringRepositoryTemplate.CopyTo(copy);
                Assert.Equal(0, launches.Count);
            }

            Directory.CreateDirectory(reference);
            RunGit(reference, "init");
            RunGit(reference, "config", "user.email", "test@example.test");
            RunGit(reference, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(reference, "README.md"), "test");
            RunGit(reference, "add", "README.md");
            RunGit(reference, "commit", "-m", "initial");

            Assert.True(Directory.Exists(Path.Combine(copy, ".git")));
            Assert.Equal("1", RunGit(copy, "rev-list", "--count", "HEAD"));
            Assert.Equal("initial", RunGit(copy, "log", "-1", "--format=%s"));
            Assert.Equal("test@example.test", RunGit(copy, "config", "--local", "user.email"));
            Assert.Equal("Test User", RunGit(copy, "config", "--local", "user.name"));
            Assert.Equal("test", File.ReadAllText(Path.Combine(copy, "README.md")));
            Assert.Equal("", RunGit(copy, "status", "--porcelain"));
            Assert.Equal(RunGit(reference, "branch", "--show-current"), RunGit(copy, "branch", "--show-current"));
        }
        finally
        {
            DeleteDirectory(warm);
            DeleteDirectory(copy);
            DeleteDirectory(reference);
        }
    }

    [Xunit.Fact]
    public void CopyTo_AfterAnotherCopyCommitsAndBranches_PreservesTemplate()
    {
        var changed = CreateTempPath();
        var other = CreateTempPath();
        var later = CreateTempPath();
        try
        {
            ProgressiveReviewSteeringRepositoryTemplate.CopyTo(changed);
            ProgressiveReviewSteeringRepositoryTemplate.CopyTo(other);
            var initialHead = RunGit(other, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(changed, "extra.txt"), "copy change");
            RunGit(changed, "add", "extra.txt");
            RunGit(changed, "commit", "-m", "copy change");
            RunGit(changed, "branch", "extra");
            Assert.Equal("2", RunGit(changed, "rev-list", "--count", "HEAD"));
            Assert.Equal("extra", RunGit(changed, "for-each-ref", "--format=%(refname:short)", "refs/heads/extra"));

            AssertUnchangedCopy(other, initialHead);
            ProgressiveReviewSteeringRepositoryTemplate.CopyTo(later);
            AssertUnchangedCopy(later, initialHead);
        }
        finally
        {
            DeleteDirectory(changed);
            DeleteDirectory(other);
            DeleteDirectory(later);
        }
    }

    private static void AssertUnchangedCopy(string root, string initialHead)
    {
        Assert.Equal("1", RunGit(root, "rev-list", "--count", "HEAD"));
        Assert.Equal(initialHead, RunGit(root, "rev-parse", "HEAD"));
        Assert.Equal(RunGit(root, "branch", "--show-current"), RunGit(root, "for-each-ref", "--format=%(refname:short)", "refs/heads"));
        Assert.False(File.Exists(Path.Combine(root, "extra.txt")));
        Assert.Equal("", RunGit(root, "status", "--porcelain"));
    }

    private static string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"mcg-steering-template-copy-{Guid.NewGuid():N}");

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
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
