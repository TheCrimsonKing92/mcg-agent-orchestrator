using Assert = Xunit.Assert;
using Factory = WorkerDispatchTestsSeededRepositoryFactory;

// Parallel-safe: each fact owns its factory, unique root, repositories and launch counter.
public sealed class WorkerDispatchTestsSeededRepositoryFixtureGitCommitLaunchCount
{
    private static readonly DateTimeOffset CommitTime = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void PublishedRepositoryCommitLaunchesGitOnce() => WithRepository((repository, _) =>
    {
        StageChange(repository);
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        Factory.RunFixtureGit(repository, ["commit", "-m", "Changed file"], CommitTime);

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void LinkedWorktreeCommitLaunchesGitOnce() => WithRepository((repository, root) =>
    {
        var linked = Path.Combine(root, "linked");
        Factory.RunFixtureGit(repository, ["worktree", "add", "-b", "linked", linked, "HEAD"], CommitTime);
        Assert.True(File.Exists(Path.Combine(linked, ".git")));
        StageChange(linked);
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        Factory.RunFixtureGit(linked, ["commit", "-m", "Linked change"], CommitTime);

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void StatusLaunchesGitOnce() => WithRepository((repository, _) =>
    {
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        Factory.RunFixtureGit(repository, ["status", "--short"], CommitTime);

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void SuccessfulCommitAndStatusEachReturnOneReceipt() => WithRepository((repository, _) =>
    {
        StageChange(repository);

        var commit = Assert.Single(Factory.RunFixtureGit(
            repository, ["commit", "-m", "Changed file"], CommitTime));
        var status = Assert.Single(Factory.RunFixtureGit(repository, ["status", "--short"], CommitTime));

        Assert.True(commit.Succeeded, commit.ToString());
        Assert.Contains("commit", commit.Arguments!);
        Assert.True(status.Succeeded, status.ToString());
        Assert.Equal(new[] { "status", "--short" }, status.Arguments!.TakeLast(2));
    });

    [Xunit.Fact]
    public void NothingToCommitThrowsWithCommitThenHeadReceiptsAndTwoLaunches() => WithRepository((repository, _) =>
    {
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        var failure = Assert.Throws<Factory.SeededRepositoryFailureException>(() =>
            Factory.RunFixtureGit(repository, ["commit", "-m", "Empty"], CommitTime));

        Assert.Equal(2, launches.Count);
        Assert.Equal(Factory.ValidationCheck.FixtureGitCommand, failure.Diagnostic.Check);
        Assert.NotNull(failure.Diagnostic.ProbeReceipts);
        Assert.Collection(failure.Diagnostic.ProbeReceipts,
            commit =>
            {
                Assert.Equal(Factory.ValidationCheck.FixtureGitCommand, commit.Check);
                Assert.Equal(new[] { "commit", "-m", "Empty" }, commit.Arguments!.TakeLast(3));
                Assert.False(commit.Succeeded);
                Assert.NotNull(commit.ExitCode);
                Assert.NotEqual(0, commit.ExitCode);
                Assert.Contains("nothing to commit", commit.StandardOutput, StringComparison.Ordinal);
            },
            head =>
            {
                Assert.Equal(Factory.ValidationCheck.FixtureGitCommand, head.Check);
                Assert.Equal(new[] { "rev-parse", "--verify", "HEAD^{commit}" }, head.Arguments!.TakeLast(3));
                Assert.True(head.Succeeded, head.ToString());
            });
        Assert.Equal(new[] { 1, 2 }, failure.Diagnostic.ProbeReceipts.Select(receipt => receipt.ProbeOrdinal));
    });

    [Xunit.Fact]
    public void CommitOutsideRepositoryThrowsSeededRepositoryFailure() => WithRepository((_, root) =>
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "not-a-repository")).FullName;
        Assert.False(Directory.Exists(Path.Combine(directory, ".git")));
        Assert.False(File.Exists(Path.Combine(directory, ".git")));

        var failure = Assert.Throws<Factory.SeededRepositoryFailureException>(() =>
            Factory.RunFixtureGit(directory, ["commit", "-m", "Missing repository"], CommitTime));

        Assert.Equal(Factory.ValidationCheck.FixtureGitCommand, failure.Diagnostic.Check);
    });

    private static void StageChange(string repository)
    {
        File.WriteAllText(Path.Combine(repository, "seed.txt"), "changed\n");
        Factory.RunFixtureGit(repository, ["add", "seed.txt"], CommitTime);
    }

    private static void WithRepository(Action<string, string> action)
    {
        var root = Directory.CreateTempSubdirectory("mcg-fixture-git-launch-").FullName;
        try
        {
            var factory = new Factory(
                () => Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName,
                template => File.WriteAllText(Path.Combine(template, "seed.txt"), "seed\n"),
                root);
            var repository = factory.Create().PublishedIdentity.RepositoryPath;
            action(repository, root);
        }
        finally
        {
            Factory.DeleteOwnedDirectory(root);
        }
    }
}
