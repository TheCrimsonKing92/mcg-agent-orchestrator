using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns a real repository, workspace and execution owner.
public sealed class AcceptanceRunExecutionContextTestsFollowerPinnedBase : GoalWorktreeTestBase
{
    [Fact]
    public async Task PendingThenExactLanding_KeepsPinnedIdentity()
    {
        var scenario = GoalWorktreeTestsFollowerWorkspace.CreateLeaderScenario();
        try
        {
            using var workspace = CreateWorkspace(scenario);
            await using var owner = CreateOwner(scenario, workspace);
            Assert.Equal(scenario.Candidate, owner.Identity.MainSha);
            owner.EnsureResolvedIdentityCurrent(workspace.Path);
            GoalWorktreeTestsFollowerWorkspace.LandLeader(scenario);
            Assert.NotEqual(scenario.Candidate, RunGitOutput(scenario.Repo, "rev-parse", "main"));
            Assert.Equal(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
            Assert.Equal(scenario.CandidateTree, RunGitOutput(scenario.Repo, "rev-parse", "main^{tree}"));
            owner.EnsureResolvedIdentityCurrent(workspace.Path);
            await using var afterLanding = CreateOwner(scenario, workspace);
            Assert.Equal(scenario.Candidate, afterLanding.Identity.MainSha);
            afterLanding.EnsureResolvedIdentityCurrent(workspace.Path);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("different-tree")]
    [InlineData("second-commit")]
    public async Task MainMovementOutsideExactLanding_InvalidatesPinnedIdentity(string movement)
    {
        var scenario = GoalWorktreeTestsFollowerWorkspace.CreateLeaderScenario();
        try
        {
            using var workspace = CreateWorkspace(scenario);
            await using var owner = CreateOwner(scenario, workspace);
            owner.EnsureResolvedIdentityCurrent(workspace.Path);
            switch (movement)
            {
                case "unrelated":
                    RunGit(scenario.Repo, "checkout", "main");
                    GoalWorktreeTestsFollowerWorkspace.WriteAndCommit(scenario.Repo, "extra.txt", "unrelated\n");
                    Assert.NotEqual(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "main"));
                    break;
                case "different-tree":
                    GoalWorktreeTestsFollowerWorkspace.WriteAndCommit(scenario.Repo, "extra.txt", "changed leader\n");
                    GoalWorktreeTestsFollowerWorkspace.LandLeader(scenario);
                    Assert.Equal(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
                    Assert.NotEqual(scenario.CandidateTree, RunGitOutput(scenario.Repo, "rev-parse", "main^{tree}"));
                    break;
                case "second-commit":
                    GoalWorktreeTestsFollowerWorkspace.LandLeader(scenario);
                    owner.EnsureResolvedIdentityCurrent(workspace.Path);
                    GoalWorktreeTestsFollowerWorkspace.WriteAndCommit(scenario.Repo, "extra.txt", "after landing\n");
                    Assert.NotEqual(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown movement: {movement}");
            }
            var error = Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => owner.EnsureResolvedIdentityCurrent(workspace.Path));
            Assert.True(error.IsChangedIdentity);
            var creationError = Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => CreateOwner(scenario, workspace));
            Assert.True(creationError.IsChangedIdentity);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public async Task UnpinnedAttempt_ExactLanding_InvalidatesIdentity()
    {
        var scenario = GoalWorktreeTestsFollowerWorkspace.CreateLeaderScenario();
        try
        {
            using var workspace = CreateWorkspace(scenario);
            await using var owner = CreateOwner(scenario, workspace, pinned: false);
            Assert.Equal(scenario.Main, owner.Identity.MainSha);
            owner.EnsureResolvedIdentityCurrent(workspace.Path);
            GoalWorktreeTestsFollowerWorkspace.LandLeader(scenario);
            var error = Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => owner.EnsureResolvedIdentityCurrent(workspace.Path));
            Assert.True(error.IsChangedIdentity);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public async Task MultiCommitCandidateFastForward_InvalidatesAndRefusesCreation()
    {
        var scenario = GoalWorktreeTestsFollowerWorkspace.CreateLeaderScenario();
        try
        {
            GoalWorktreeTestsFollowerWorkspace.WriteAndCommit(scenario.Repo, "extra.txt", "second leader commit\n");
            scenario = scenario with
            {
                Candidate = RunGitOutput(scenario.Repo, "rev-parse", "HEAD"),
                CandidateTree = RunGitOutput(scenario.Repo, "rev-parse", "HEAD^{tree}")
            };
            using var workspace = CreateWorkspace(scenario);
            await using var owner = CreateOwner(scenario, workspace);
            owner.EnsureResolvedIdentityCurrent(workspace.Path);
            RunGit(scenario.Repo, "checkout", "main");
            RunGit(scenario.Repo, "merge", "--ff-only", "leader");
            Assert.Equal(scenario.Candidate, RunGitOutput(scenario.Repo, "rev-parse", "main"));
            Assert.NotEqual(scenario.Main, RunGitOutput(scenario.Repo, "rev-parse", "main^1"));
            Assert.True(Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => owner.EnsureResolvedIdentityCurrent(workspace.Path)).IsChangedIdentity);
            Assert.True(Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => CreateOwner(scenario, workspace)).IsChangedIdentity);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    [Fact]
    public void UnresolvedMain_RefusesPinnedCreation()
    {
        var scenario = GoalWorktreeTestsFollowerWorkspace.CreateLeaderScenario();
        try
        {
            using var workspace = CreateWorkspace(scenario);
            RunGit(scenario.Repo, "update-ref", "-d", "refs/heads/main");
            var error = Assert.Throws<AcceptanceExecutionIdentityChangedException>(
                () => CreateOwner(scenario, workspace));
            Assert.False(error.IsChangedIdentity);
        }
        finally { DeleteDirectory(scenario.Repo); }
    }

    private static FollowerGateWorkspace CreateWorkspace(GoalWorktreeTestsFollowerWorkspace.Scenario scenario) =>
        Assert.IsType<FollowerGateWorkspace>(GoalWorktreeTestsFollowerWorkspace.CreateFollower(scenario).Workspace);

    private static AcceptanceAttemptExecutionOwner CreateOwner(
        GoalWorktreeTestsFollowerWorkspace.Scenario scenario, FollowerGateWorkspace workspace, bool pinned = true) =>
        Assert.IsType<AcceptanceAttemptExecutionOwner>(AcceptanceExecutionOwners.CreateAttempt(workspace.Path,
            scenario.Goal, options: new AcceptanceRunExecutionOptions(
                ResultsPrefix: Path.Combine(scenario.Repo, "results", Guid.NewGuid().ToString("N")),
                PinnedBase: pinned ? new(scenario.Main, scenario.Candidate, scenario.CandidateTree) : null)));
}
