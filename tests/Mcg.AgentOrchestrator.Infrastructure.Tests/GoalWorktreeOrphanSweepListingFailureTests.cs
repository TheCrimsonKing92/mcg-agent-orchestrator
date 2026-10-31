using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories and instance hooks; no ambient mutation.
public sealed class GoalWorktreeOrphanSweepListingFailureTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("nonzero")]
    [Xunit.InlineData("timeout")]
    [Xunit.InlineData("drain-timeout")]
    [Xunit.InlineData("not-started")]
    [Xunit.InlineData("execution-failure")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("truncated")]
    public void FailedListing_KeepsEveryCandidate_WarnsOnceAndRecovers(string failure)
    {
        var repo = CreateSeededRepository();
        try
        {
            var fixture = new OrphanSweepTestFixture(repo);
            var paths = new[] { fixture.AddDirectory("orphan-one"), fixture.AddDirectory("orphan-two") };
            var calls = 0;
            var hooks = fixture.Hooks with
            {
                RunWorktreeList = root => ++calls == 1
                    ? Failure(failure, root)
                    : GoalWorktreeRegistrationListing.RunGit(root)
            };

            var failedPass = fixture.Sweep(hooks: hooks);

            Assert.Equal(1, calls);
            Assert.Equal(0, failedPass.RemovedCount);
            Assert.Empty(failedPass.LeftoverPaths);
            Assert.Empty(fixture.Deletions);
            Assert.Equal(2, fixture.Outcomes.Count);
            Assert.Equal("orphan-sweep:listing-failed", Assert.Single(fixture.Warnings).Operation);
            foreach (var path in paths)
            {
                Assert.True(File.Exists(Path.Combine(path, "nested", "content.txt")));
                fixture.AssertOutcome(path, "listing-failed");
                Assert.Null(GoalWorktrees.TryGetCleanupBackoff(path, hooks));
                Assert.False(HasAnyCleanupNeededRecord(repo, path));
            }

            fixture.Outcomes.Clear();
            var recoveredPass = fixture.Sweep(hooks: hooks);

            Assert.Equal(2, calls);
            Assert.Equal(2, recoveredPass.RemovedCount);
            Assert.Empty(recoveredPass.LeftoverPaths);
            Assert.Equal(2, fixture.Deletions.Count);
            Assert.Equal(2, fixture.Outcomes.Count);
            Assert.Single(fixture.Warnings); // Recovery adds no warning.
            foreach (var path in paths)
            {
                Assert.False(Directory.Exists(path));
                fixture.AssertOutcome(path, "removed");
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void SuccessfulEmptyListing_RemovesAgedOrphan_WithoutWarning()
    {
        var repo = CreateSeededRepository();
        try
        {
            var fixture = new OrphanSweepTestFixture(repo);
            var path = fixture.AddDirectory("unregistered-orphan");
            var calls = 0;
            var result = fixture.Sweep(hooks: fixture.Hooks with
            {
                RunWorktreeList = _ => { calls++; return new(0, "", ""); }
            });

            Assert.Equal(1, calls);
            Assert.Equal(1, result.RemovedCount);
            Assert.False(Directory.Exists(path));
            Assert.Equal(path, Assert.Single(fixture.Deletions));
            Assert.Single(fixture.Outcomes);
            fixture.AssertOutcome(path, "removed");
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }

    private static GitCli.GitResult Failure(string failure, string root) => failure switch
    {
        "nonzero" => new(1, "", "forced listing failure"),
        "timeout" => new(-1, "", "forced git timeout"),
        "drain-timeout" => new(0, "", "forced drain timeout", DrainTimedOut: true),
        "not-started" => new(0, "", "forced start failure", ProcessStarted: false),
        "execution-failure" => throw new IOException("forced execution failure"),
        "malformed" => new(0, "not porcelain", ""),
        "truncated" => new(0, $"worktree {root}\nHEAD {new string('a', 40)}\n", ""),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}
