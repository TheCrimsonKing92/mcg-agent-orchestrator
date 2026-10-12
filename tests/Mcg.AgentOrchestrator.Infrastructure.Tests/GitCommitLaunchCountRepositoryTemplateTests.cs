// Parallel-safe: each fact owns its copies under a GUID root; the shared template is read-only.
public sealed class GitCommitLaunchCountRepositoryTemplateTests
{
    [Xunit.Fact]
    public void CopyTo_WarmTemplate_LaunchesZeroProbesWhileFreshSetupLaunchesFour()
    {
        WithTemporaryRoot(root =>
        {
            GitCommitLaunchCountRepositoryTemplate.CopyTo(Path.Combine(root, "warm"));

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                GitCommitLaunchCountRepositoryTemplate.CopyTo(Path.Combine(root, "copy"));
                Assert.Equal(0, launches.Count);
            }

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                CreateFreshRepository(Path.Combine(root, "fresh"));
                Assert.Equal(4, launches.Count);
            }
        });
    }

    [Xunit.Fact]
    public void CopyTo_FreshRepository_PreservesMetadataConfigurationAndUnbornHead()
    {
        WithTemporaryRoot(root =>
        {
            var copy = Path.Combine(root, "copy");
            var fresh = Path.Combine(root, "fresh");
            GitCommitLaunchCountRepositoryTemplate.CopyTo(copy);
            CreateFreshRepository(fresh);
            var copyMetadata = Path.Combine(copy, ".git");
            var freshMetadata = Path.Combine(fresh, ".git");

            // Compare untouched metadata before status or other queries can create incidental files.
            var freshFiles = RelativeFiles(freshMetadata);
            Assert.NotEmpty(freshFiles);
            Assert.Equal(freshFiles, RelativeFiles(copyMetadata));
            foreach (var file in freshFiles)
                Assert.Equal(File.ReadAllBytes(Path.Combine(freshMetadata, file)),
                    File.ReadAllBytes(Path.Combine(copyMetadata, file)));

            Assert.Equal(RelativeDirectories(freshMetadata), RelativeDirectories(copyMetadata));
            foreach (var directory in new[] { "refs/heads", "refs/tags", "objects/info", "objects/pack" })
                Assert.True(Directory.Exists(Path.Combine(copyMetadata, directory)), directory);

            var freshHead = File.ReadAllText(Path.Combine(freshMetadata, "HEAD"));
            Assert.Equal(freshHead, File.ReadAllText(Path.Combine(copyMetadata, "HEAD")));
            Assert.Equal("ref: refs/heads/main", freshHead.TrimEnd('\r', '\n'));
            Assert.Equal(ProbeGit(fresh, "config", "--local", "--list"),
                ProbeGit(copy, "config", "--local", "--list"));
            Assert.Empty(ProbeGit(fresh, "status", "--porcelain=v1"));
            Assert.Empty(ProbeGit(copy, "status", "--porcelain=v1"));
            AssertUnbornHead(fresh);
            AssertUnbornHead(copy);
        });
    }

    [Xunit.Fact]
    public void CopyTo_CommitInOneCopy_LeavesOtherCopyAndTemplateUnborn()
    {
        WithTemporaryRoot(root =>
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            GitCommitLaunchCountRepositoryTemplate.CopyTo(first);
            GitCommitLaunchCountRepositoryTemplate.CopyTo(second);
            AssertUnbornHead(first);
            AssertUnbornHead(second);
            AssertUnbornHead(GitCommitLaunchCountRepositoryTemplate.TemplateDirectory);

            File.WriteAllText(Path.Combine(first, "seed.txt"), "seed");
            ProbeGit(first, "add", "seed.txt");
            ProbeGit(first, "commit", "-m", "Seed");

            Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(first, out var head));
            Assert.NotNull(head);
            AssertUnbornHead(second);
            AssertUnbornHead(GitCommitLaunchCountRepositoryTemplate.TemplateDirectory);
        });
    }

    private static void CreateFreshRepository(string repository)
    {
        Directory.CreateDirectory(repository);
        ProbeGit(repository, "init", "--initial-branch=main");
        ProbeGit(repository, "config", "user.email", "tests@example.com");
        ProbeGit(repository, "config", "user.name", "Worktree Tests");
        ProbeGit(repository, "config", "commit.gpgsign", "false");
    }

    private static string ProbeGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput;
    }

    private static void AssertUnbornHead(string repository)
    {
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(repository, out var head));
        Assert.Null(head);
    }

    private static string[] RelativeFiles(string metadata) =>
        Directory.EnumerateFiles(metadata, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(metadata, file))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static string[] RelativeDirectories(string metadata) =>
        Directory.EnumerateDirectories(metadata, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(metadata, directory))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static void WithTemporaryRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-git-commit-template-tests-" + Guid.NewGuid().ToString("n"));
        try
        {
            test(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
