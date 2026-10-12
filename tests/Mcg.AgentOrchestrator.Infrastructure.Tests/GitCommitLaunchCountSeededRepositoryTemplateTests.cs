// Parallel-safe: each fact owns its copies under a GUID root; the shared template is read-only.
public sealed class GitCommitLaunchCountSeededRepositoryTemplateTests
{
    [Xunit.Fact]
    public void CopyTo_WarmTemplate_LaunchesZeroProbesWhileFreshSetupLaunchesSix()
    {
        WithTemporaryRoot(root =>
        {
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(Path.Combine(root, "warm"));

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(Path.Combine(root, "copy"));
                Assert.Equal(0, launches.Count);
            }

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                CreateFreshSeededRepository(Path.Combine(root, "fresh"));
                Assert.Equal(6, launches.Count);
            }
        });
    }

    [Xunit.Fact]
    public void CopyTo_SeededRepository_PreservesCommittedStateAndConfiguration()
    {
        WithTemporaryRoot(root =>
        {
            var copy = Path.Combine(root, "copy");
            var fresh = Path.Combine(root, "fresh");
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(copy);
            CreateFreshSeededRepository(fresh);

            foreach (var repository in new[] { copy, fresh })
            {
                Assert.Equal("1", ProbeGit(repository, "rev-list", "--count", "HEAD").TrimEnd('\r', '\n'));
                Assert.Equal("Seed", ProbeGit(repository, "log", "-1", "--format=%s").TrimEnd('\r', '\n'));
                Assert.Empty(ProbeGit(repository, "status", "--porcelain=v1"));
                Assert.Equal("seed", File.ReadAllText(Path.Combine(repository, "seed.txt")));
                AssertHeadMatchesProbe(repository);
                AssertSeedObjectCounts(repository);
            }

            Assert.Equal(ProbeGit(fresh, "ls-tree", "-r", "HEAD"), ProbeGit(copy, "ls-tree", "-r", "HEAD"));
            Assert.Equal(ProbeGit(fresh, "config", "--local", "--list").TrimEnd('\r', '\n'),
                ProbeGit(copy, "config", "--local", "--list").TrimEnd('\r', '\n'));
        });
    }

    [Xunit.Fact]
    public void CopyTo_SecondAndEmptyCommits_PreserveOneAndTwoLaunches()
    {
        WithTemporaryRoot(root =>
        {
            var changed = Path.Combine(root, "changed");
            var clean = Path.Combine(root, "clean");
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(changed);
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(clean);
            AssertHeadMatchesProbe(changed);
            AssertHeadMatchesProbe(clean);
            File.WriteAllText(Path.Combine(changed, "seed.txt"), "second");
            ProbeGit(changed, "add", "--all");

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                var result = InfrastructureTestSupport.RunGitWithCommitPostcondition(changed, ["commit", "-m", "Second"]);
                InfrastructureTestSupport.RequireCompleteGitOutput(result);
                Assert.True(result.Succeeded, result.ToString());
                Assert.Equal(1, launches.Count);
            }

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    InfrastructureTestSupport.RunGitWithCommitPostcondition(clean, ["commit", "-m", "Empty"]));
                Assert.Contains("nothing to commit", exception.Message, StringComparison.Ordinal);
                Assert.Equal(2, launches.Count);
            }
        });
    }

    [Xunit.Fact]
    public void CopyTo_CommitInOneCopy_LeavesOtherCopyAndTemplateAtSeed()
    {
        WithTemporaryRoot(root =>
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(first);
            GitCommitLaunchCountSeededRepositoryTemplate.CopyTo(second);
            var template = GitCommitLaunchCountSeededRepositoryTemplate.TemplateDirectory;
            var templateHead = AssertHeadMatchesProbe(template);
            Assert.Equal(templateHead, AssertHeadMatchesProbe(first));
            Assert.Equal(templateHead, AssertHeadMatchesProbe(second));

            File.WriteAllText(Path.Combine(first, "seed.txt"), "second");
            ProbeGit(first, "add", "--all");
            ProbeGit(first, "commit", "-m", "Second");

            Assert.NotEqual(templateHead, AssertHeadMatchesProbe(first));
            Assert.Equal("2", ProbeGit(first, "rev-list", "--count", "HEAD").TrimEnd('\r', '\n'));
            foreach (var repository in new[] { second, template })
            {
                Assert.Equal(templateHead, AssertHeadMatchesProbe(repository));
                Assert.Equal("1", ProbeGit(repository, "rev-list", "--count", "HEAD").TrimEnd('\r', '\n'));
            }
        });
    }

    private static void CreateFreshSeededRepository(string repository)
    {
        Directory.CreateDirectory(repository);
        ProbeGit(repository, "init", "--initial-branch=main");
        ProbeGit(repository, "config", "user.email", "tests@example.com");
        ProbeGit(repository, "config", "user.name", "Worktree Tests");
        ProbeGit(repository, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repository, "seed.txt"), "seed");
        ProbeGit(repository, "add", "--all");
        ProbeGit(repository, "commit", "-m", "Seed");
    }

    private static string ProbeGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput;
    }

    private static string AssertHeadMatchesProbe(string repository)
    {
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(repository, out var head));
        Assert.NotNull(head);
        Assert.Equal(InfrastructureTestSupport.TryGetGitHead(repository), head);
        return head;
    }

    private static void AssertSeedObjectCounts(string repository)
    {
        var lines = ProbeGit(repository, "cat-file", "--batch-all-objects", "--batch-check")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var types = lines.Select(line =>
        {
            var fields = line.TrimEnd('\r').Split(' ');
            Assert.Equal(3, fields.Length);
            return fields[1];
        }).ToArray();
        Assert.Equal(1, types.Count(type => type == "commit"));
        Assert.Equal(1, types.Count(type => type == "tree"));
        Assert.Equal(1, types.Count(type => type == "blob"));
    }

    private static void WithTemporaryRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-git-commit-seeded-template-tests-" + Guid.NewGuid().ToString("n"));
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
