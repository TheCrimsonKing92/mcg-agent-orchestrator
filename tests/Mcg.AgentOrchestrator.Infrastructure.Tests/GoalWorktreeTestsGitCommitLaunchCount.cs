using GitProbeResult = WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult;

// Parallel-safe: each fact owns a GUID-named temporary root, including linked worktrees.
public sealed class GoalWorktreeTestsGitCommitLaunchCount : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void FirstCommitLaunchesGitOnce() => WithRepository((repo, _) =>
    {
        StageChange(repo);
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        RunGit(repo, "commit", "-m", "First");

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void SecondCommitLaunchesGitOnce() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        StageChange(repo, "second");
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        RunGit(repo, "commit", "-m", "Second");

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void LinkedWorktreeCommitLaunchesGitOnce() => WithRepository((repo, root) =>
    {
        var linked = AddLinkedWorktree(repo, root);
        Assert.True(File.Exists(Path.Combine(linked, ".git")));
        StageChange(linked, "linked change");
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        RunGit(linked, "commit", "-m", "Linked");

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void StatusLaunchesGitOnce() => WithRepository((repo, _) =>
    {
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        RunGit(repo, "status", "--short");

        Assert.Equal(1, launches.Count);
    });

    [Xunit.Fact]
    public void NothingToCommitThrowsWithStdoutAndTwoLaunches() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        var expectedExit = RunGitExitCode(repo, ["commit", "-m", "Empty"], out var stdout, out _);
        Assert.NotEqual(0, expectedExit);
        Assert.Contains("nothing to commit", stdout, StringComparison.Ordinal);
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        var exception = Assert.Throws<InvalidOperationException>(() => RunGit(repo, "commit", "-m", "Empty"));

        Assert.Contains(stdout.Trim(), exception.Message, StringComparison.Ordinal);
        Assert.Contains($"exit={expectedExit}", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, launches.Count);
    });

    [Xunit.Fact]
    public void CompletedCommitWithBlankFailureIsAcceptedWithoutRetry() => WithRepository((repo, _) =>
    {
        StageChange(repo);
        var calls = 0;

        var result = InfrastructureTestSupport.RunGitWithCommitPostcondition(repo, ["commit", "-m", "First"],
            (directory, arguments) =>
            {
                calls++;
                var real = InfrastructureTestSupport.RunGitProbe(directory, arguments);
                Assert.True(real.Succeeded, real.ToString());
                return real with { ExitCode = 1, StandardOutput = "", StandardError = "" };
            });

        Assert.Equal(1, calls);
        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(InfrastructureTestSupport.TryGetGitHead(repo));
    });

    [Xunit.Fact]
    public void BlankCommitFailureRetriesOnceThenThrowsLastAttempt() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        var calls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            InfrastructureTestSupport.RunGitWithCommitPostcondition(repo, ["commit", "-m", "Missing"],
                (_, _) => BlankFailure() with { ExitCode = 10 + ++calls }));

        Assert.Equal(2, calls);
        Assert.Equal($"git commit -m Missing failed: exit=12{Environment.NewLine}" +
                     $"stdout: {Environment.NewLine}stderr: ", exception.Message);
    });

    [Xunit.Fact]
    public void CommittedButDirtyTreeIsRejected() => WithRepository((repo, _) =>
    {
        StageChange(repo);
        var calls = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            InfrastructureTestSupport.RunGitWithCommitPostcondition(repo, ["commit", "-m", "First"],
                (directory, arguments) =>
                {
                    calls++;
                    var real = InfrastructureTestSupport.RunGitProbe(directory, arguments);
                    Assert.True(real.Succeeded, real.ToString());
                    File.WriteAllText(Path.Combine(repo, "untracked.txt"), "dirty");
                    return real with { ExitCode = 1, StandardOutput = "reported failure", StandardError = "" };
                }));

        Assert.Equal(1, calls);
        Assert.Contains("stdout: reported failure", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(InfrastructureTestSupport.TryGetGitHead(repo));
    });

    [Xunit.Fact]
    public void BlankNonCommitFailureIsNotRetried() => WithRepository((repo, _) =>
    {
        var calls = 0;
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        Assert.Throws<InvalidOperationException>(() =>
            InfrastructureTestSupport.RunGitWithCommitPostcondition(repo, ["status"], (_, _) =>
            {
                calls++;
                return BlankFailure();
            }));

        Assert.Equal(1, calls);
        Assert.Equal(0, launches.Count);
    });

    [Xunit.Fact]
    public void MissingExitCodeThrowsBeforePostconditionOrRetry() => WithRepository((repo, _) =>
    {
        var calls = 0;
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            InfrastructureTestSupport.RunGitWithCommitPostcondition(repo, ["commit"], (_, _) =>
            {
                calls++;
                return BlankFailure() with { ExitCode = null };
            }));

        Assert.Equal(1, calls);
        Assert.Equal(0, launches.Count);
        Assert.Equal("git commit produced no exit code: classification=NotRun; processStarted=True; " +
                     "timedOut=False; drainTimedOut=False; drainFailed=False; stdoutBytes=0; stderrBytes=0; stderr=",
            exception.Message);
    });

    [Xunit.Fact]
    public void UnbornHeadMatchesProbeWithoutLaunching() => WithRepository((repo, _) =>
        AssertHeadReadMatchesProbe(repo, unborn: true));

    [Xunit.Fact]
    public void BranchHeadMatchesProbeWithoutLaunching() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        AssertHeadReadMatchesProbe(repo);
    });

    [Xunit.Fact]
    public void DetachedHeadMatchesProbeWithoutLaunching() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        RunGit(repo, "checkout", "--detach");
        AssertHeadReadMatchesProbe(repo);
    });

    [Xunit.Fact]
    public void LinkedWorktreeHeadMatchesProbeWithoutLaunching() => WithRepository((repo, root) =>
    {
        var linked = AddLinkedWorktree(repo, root);
        Assert.True(File.Exists(Path.Combine(linked, ".git")));
        AssertHeadReadMatchesProbe(linked);
    });

    [Xunit.Fact]
    public void PackedBranchHeadMatchesProbeWithoutLaunching() => WithRepository((repo, _) =>
    {
        CommitSeed(repo);
        var loose = Path.Combine(repo, ".git", "refs", "heads", "main");
        Assert.True(File.Exists(loose));
        RunGit(repo, "pack-refs", "--all");
        Assert.False(File.Exists(loose));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "packed-refs")));

        AssertHeadReadMatchesProbe(repo);
    });

    [Xunit.Fact]
    public void SubdirectoryFallsBackToProbeBeforeCommit() => WithRepository((repo, _) =>
    {
        StageChange(repo);
        var subdirectory = Path.Combine(repo, "nested");
        Directory.CreateDirectory(subdirectory);
        Assert.False(InfrastructureTestSupport.TryReadGitHeadInProcess(subdirectory, out _));
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        RunGit(subdirectory, "commit", "-m", "First");

        Assert.Equal(2, launches.Count);
    });

    [Xunit.Theory]
    [Xunit.InlineData("malformed-head")]
    [Xunit.InlineData("malformed-loose-ref")]
    [Xunit.InlineData("symref-chain")]
    [Xunit.InlineData("malformed-packed-refs")]
    [Xunit.InlineData("reftable")]
    public void UnsupportedMetadataIsUnknownWithoutLaunching(string layout) => WithRepository((repo, _) =>
    {
        var metadata = Path.Combine(repo, ".git");
        switch (layout)
        {
            case "malformed-head":
                File.WriteAllText(Path.Combine(metadata, "HEAD"), "unexpected");
                break;
            case "malformed-loose-ref":
            case "symref-chain":
                File.WriteAllText(Path.Combine(metadata, "refs", "heads", "main"),
                    layout == "symref-chain" ? "ref: refs/heads/missing" : "not-an-object-id");
                break;
            case "malformed-packed-refs":
                File.WriteAllText(Path.Combine(metadata, "packed-refs"), "invalid refs/heads/main");
                break;
            case "reftable":
                Directory.CreateDirectory(Path.Combine(metadata, "reftable"));
                break;
            default:
                throw new InvalidOperationException($"Unknown test layout: {layout}");
        }
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        Assert.False(InfrastructureTestSupport.TryReadGitHeadInProcess(repo, out var head));

        Assert.Null(head);
        Assert.Equal(0, launches.Count);
    });

    [Xunit.Fact]
    public void NestedLaunchCounterRestoresOuterScope() => WithRepository((repo, _) =>
    {
        using var outer = InfrastructureTestSupport.CountGitProbeLaunches();
        RunGit(repo, "status", "--short");
        var inner = InfrastructureTestSupport.CountGitProbeLaunches();
        using (inner)
        {
            RunGit(repo, "status", "--short");
            Assert.Equal(1, inner.Count);
            Assert.Equal(1, outer.Count);
        }

        RunGit(repo, "status", "--short");

        Assert.Equal(1, inner.Count);
        Assert.Equal(2, outer.Count);
    });

    private static GitProbeResult BlankFailure() => new("git", true, 1, "", "", false, false);

    private static void AssertHeadReadMatchesProbe(string repo, bool unborn = false)
    {
        string? head;
        using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
        {
            Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(repo, out head));
            Assert.Equal(0, launches.Count);
        }

        Assert.Equal(InfrastructureTestSupport.TryGetGitHead(repo), head);
        if (unborn)
            Assert.Null(head);
        else
            Assert.NotNull(head);
    }

    private static string AddLinkedWorktree(string repo, string root)
    {
        CommitSeed(repo);
        var linked = Path.Combine(root, "linked");
        RunGit(repo, "worktree", "add", "-b", "linked", linked, "HEAD");
        return linked;
    }

    private static void StageChange(string repo, string contents = "seed")
    {
        File.WriteAllText(Path.Combine(repo, "seed.txt"), contents);
        RunGit(repo, "add", "--all");
    }

    private static void CommitSeed(string repo)
    {
        StageChange(repo);
        RunGit(repo, "commit", "-m", "Seed");
    }

    private static void WithRepository(Action<string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-git-commit-launch-" + Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            RunGit(repo, "init", "--initial-branch=main");
            RunGit(repo, "config", "user.email", "tests@example.com");
            RunGit(repo, "config", "user.name", "Worktree Tests");
            RunGit(repo, "config", "commit.gpgsign", "false");
            test(repo, root);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }
}
