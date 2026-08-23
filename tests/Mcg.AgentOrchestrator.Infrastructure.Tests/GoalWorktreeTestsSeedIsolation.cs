public sealed class GoalWorktreeTestsSeedIsolation : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void ProcessGenerationsDoNotReuseContainerNamesWhenCounterRestarts()
    {
        var firstGeneration = BuildSeedRepositoryContainerName(processStartTimeUtcTicks: 1, sequence: 1);
        var secondGeneration = BuildSeedRepositoryContainerName(processStartTimeUtcTicks: 2, sequence: 1);

        Assert.NotEqual(firstGeneration, secondGeneration);
    }

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
                AssertSeedHeadResolves(repo);
            }

            foreach (var repo in createdRepos[..2])
            {
                DeleteDirectory(repo);
            }

            foreach (var repo in createdRepos[2..])
            {
                Assert.True(File.Exists(Path.Combine(repo, "seed.txt")));
                AssertSeedHeadResolves(repo);
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

    [Xunit.Fact]
    public async Task ConcurrentSeedCreationRepeatedlyProducesResolvableHeads()
    {
        const int roundCount = 6;
        const int creatorCount = 6;

        for (var round = 0; round < roundCount; round++)
        {
            var repos = new string?[creatorCount];
            using var startGate = new Barrier(creatorCount);
            try
            {
                var creators = Enumerable.Range(0, creatorCount)
                    .Select(index => Task.Run(() =>
                    {
                        if (!startGate.SignalAndWait(TimeSpan.FromSeconds(30)))
                        {
                            throw new TimeoutException("Concurrent seed creators did not reach the start gate.");
                        }

                        repos[index] = CreateSeededGitRepositoryForIsolation();
                    }))
                    .ToArray();
                await Task.WhenAll(creators);

                foreach (var repo in repos.Select(repo => repo!))
                {
                    AssertSeedHeadResolves(repo);
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

    private static void AssertSeedHeadResolves(string repo)
    {
        var head = ReadSeedHeadCommitId(repo);
        if (head.ExitCode == 0 && head.Stdout.Trim().Length > 0)
        {
            return;
        }

        Assert.Fail(
            $"Seed repository '{repo}' reported no HEAD commit (exit={head.ExitCode}, stdoutLength={head.Stdout.Length}). " +
            DescribeSeedHeadState(repo));
    }
}
