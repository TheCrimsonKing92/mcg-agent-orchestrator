public sealed class GoalWorktreeTestsSeedIsolation : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void SeedRepositoriesDoNotShareAMutableParentDirectory()
    {
        var firstRepo = CreateSeededRepository();
        var secondRepo = CreateSeededRepository();
        try
        {
            var firstContainer = Path.GetDirectoryName(firstRepo)!;
            var secondContainer = Path.GetDirectoryName(secondRepo)!;

            Assert.NotEqual(NormalizePath(firstContainer), NormalizePath(secondContainer));
            Assert.Equal(
                NormalizePath(SeedRepositoryProcessRootPath),
                NormalizePath(Path.GetDirectoryName(firstContainer)!));
            Assert.Equal(
                NormalizePath(SeedRepositoryProcessRootPath),
                NormalizePath(Path.GetDirectoryName(secondContainer)!));
        }
        finally
        {
            DeleteDirectory(firstRepo);
            DeleteDirectory(secondRepo);
        }
    }

    [Xunit.Fact]
    public void DeletingOneSeedRepositoryLeavesOtherTreesIntact()
    {
        var firstRepo = CreateSeededRepository();
        var secondRepo = CreateSeededRepository();
        var firstContainer = Path.GetDirectoryName(firstRepo)!;
        var secondContainer = Path.GetDirectoryName(secondRepo)!;
        var repoSentinel = Path.Combine(secondRepo, "repo-sentinel.txt");
        var containerSentinel = Path.Combine(secondContainer, "container-sentinel.txt");
        try
        {
            File.WriteAllText(repoSentinel, "repo");
            File.WriteAllText(containerSentinel, "container");

            DeleteDirectory(firstRepo);

            Assert.False(Directory.Exists(firstRepo));
            Assert.False(Directory.Exists(firstContainer));
            Assert.True(File.Exists(repoSentinel));
            Assert.True(File.Exists(containerSentinel));
        }
        finally
        {
            DeleteDirectory(firstRepo);
            DeleteDirectory(secondRepo);
        }
    }

    [Xunit.Fact]
    public void SeedRootIsOwnedByTheCurrentProcess()
    {
        var repo = CreateSeededRepository();
        try
        {
            var container = Path.GetDirectoryName(repo)!;

            Assert.Equal($"p{Environment.ProcessId:x}", Path.GetFileName(SeedRepositoryProcessRootPath));
            Assert.Equal(
                NormalizePath(SeedRepositoryProcessRootPath),
                NormalizePath(Path.GetDirectoryName(container)!));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task ConcurrentSeedCreationAllocatesDisjointContainers()
    {
        const int creatorCount = 4;
        var repos = new string?[creatorCount];
        try
        {
            var creators = Enumerable.Range(0, creatorCount)
                .Select(index => Task.Run(() => repos[index] = CreateSeededRepository()))
                .ToArray();
            await Task.WhenAll(creators);

            var createdRepos = repos.Select(repo => repo!).ToArray();
            var containers = createdRepos.Select(repo => Path.GetDirectoryName(repo)!).ToArray();
            Assert.Equal(
                creatorCount,
                containers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var repo in createdRepos)
            {
                Assert.True(File.Exists(Path.Combine(repo, "seed.txt")));
                Assert.NotEmpty(RunGitOutput(repo, "rev-parse", "HEAD").Trim());
            }

            foreach (var repo in createdRepos[..2])
            {
                DeleteDirectory(repo);
            }

            foreach (var repo in createdRepos[2..])
            {
                Assert.True(File.Exists(Path.Combine(repo, "seed.txt")));
                Assert.NotEmpty(RunGitOutput(repo, "rev-parse", "HEAD").Trim());
            }
        }
        finally
        {
            foreach (var repo in repos)
            {
                if (repo is not null)
                {
                    DeleteDirectory(repo);
                }
            }
        }
    }
}
