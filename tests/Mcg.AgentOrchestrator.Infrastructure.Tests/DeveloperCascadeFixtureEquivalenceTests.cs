using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static DeveloperCascadeDispatchTests;

// Parallel-safe: this class owns a separate linked worktree from the dispatch class.
// xunit serializes cases within this class, including the tracked-file negative control.
public sealed class DeveloperCascadeFixtureEquivalenceTests : WorkerDispatchTestSupport
{
    private static readonly string[] FixedRetries =
    [
        RatchetRetry,
        Prefix + FirstLine,
        "Recheck every call site."
    ];

    [Fact]
    public void RetrySequence_SharedAndFreshWorktrees_ProduceEqualRoutes()
    {
        using var shared = CreateSharedFixture();
        using var fresh = new Fixture(perTestRepository: true);
        Assert.NotEqual(shared.WorkingDirectory, fresh.WorkingDirectory);

        var sharedRoutes = PrepareSequence(shared, FixedRetries);
        var freshRoutes = PrepareSequence(fresh, FixedRetries);

        Assert.True(sharedRoutes.Count >= 4);
        Assert.Equal(freshRoutes, sharedRoutes);
        Assert.EndsWith("cascade=primary rule=first-round", sharedRoutes[0].ModelSelectionReason);
        Assert.Contains("cascade=cheap rule=structural-ratchet", sharedRoutes[1].ModelSelectionReason);
        Assert.EndsWith("cascade=escalated rule=mechanical-finding-persisted", sharedRoutes[2].ModelSelectionReason);
        // Prior escalation remains sticky even when the next retry text is non-mechanical.
        Assert.EndsWith("cascade=primary rule=prior-escalation", sharedRoutes[3].ModelSelectionReason);
    }

    [Fact]
    public void ConsecutiveGoals_SharedWorktree_MatchIndependentFreshRoutes()
    {
        GoalId firstGoal;
        string workingDirectory;
        using (var first = CreateSharedFixture())
        using (var freshFirst = new Fixture(perTestRepository: true))
        {
            firstGoal = first.Goal.Id;
            workingDirectory = first.WorkingDirectory;
            Assert.Equal(PrepareSequence(freshFirst, FixedRetries), PrepareSequence(first, FixedRetries));
            first.AssertWorktreeClean();
        }

        string[] differentRetries = [Prefix + OtherLine, "Recheck the independent retry seam."];
        using (var second = CreateSharedFixture())
        using (var freshSecond = new Fixture(perTestRepository: true))
        {
            Assert.NotEqual(firstGoal, second.Goal.Id);
            Assert.Equal(workingDirectory, second.WorkingDirectory);
            Assert.NotEqual(FixedRetries[0], differentRetries[0]);
            Assert.Equal(PrepareSequence(freshSecond, differentRetries), PrepareSequence(second, differentRetries));
            second.AssertWorktreeClean();
        }
    }

    [Fact]
    public void TrackedFileChange_SharedWorktree_BlocksPreparationAndFailsCleanCheck()
    {
        using var shared = CreateSharedFixture();
        shared.AssertWorktreeClean();
        var path = Path.Combine(shared.WorkingDirectory, "seed.txt");
        var original = File.ReadAllBytes(path);
        try
        {
            File.AppendAllText(path, "\nCascade fixture negative control.\n");
            var status = shared.InspectWorktree();
            Assert.True(status.Succeeded, status.Error);
            Assert.True(status.IsDirty);
            Assert.Contains("seed.txt", status.CommitWorthyPaths);
            var cleanFailure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => shared.AssertWorktreeClean());
            Assert.Contains("seed.txt", cleanFailure.Message);

            var preflight = Assert.Throws<WorkerSubscriptionPreflightException>(() => shared.Prepare());
            Assert.Contains("blocked: worktree has 1 uncommitted change(s)", preflight.Message);
            Assert.Contains("seed.txt", preflight.Message);
            Assert.Null(shared.Task.LastDispatch);
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }
        shared.AssertWorktreeClean();
    }

    [Fact]
    public void ThreeSharedFixtures_CreateOneLinkedWorktree()
    {
        using var first = CreateSharedFixture();
        using var second = CreateSharedFixture();
        using var third = CreateSharedFixture();

        Assert.Equal(1, SharedWorktree<DeveloperCascadeFixtureEquivalenceTests>.CreationCount);
        Assert.Equal(first.WorkingDirectory, second.WorkingDirectory);
        Assert.Equal(first.WorkingDirectory, third.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(first.WorkingDirectory, ".git")));
        Assert.NotEqual(first.Root, second.Root);
        Assert.NotEqual(first.Goal.Id, second.Goal.Id);
    }

    private static Fixture CreateSharedFixture() =>
        new(sharedWorktree: SharedWorktree<DeveloperCascadeFixtureEquivalenceTests>.Value);

    private static List<Route> PrepareSequence(Fixture fixture, IEnumerable<string> retries)
    {
        var routes = new List<Route>();
        fixture.Prepare();
        routes.Add(ReadRoute(fixture.Task));
        foreach (var retry in retries)
        {
            fixture.Retry(retry);
            fixture.Prepare();
            routes.Add(ReadRoute(fixture.Task));
        }
        return routes;
    }

    private static Route ReadRoute(TaskSpec task)
    {
        var dispatch = task.LastDispatch;
        Assert.NotNull(dispatch);
        return new(dispatch.WorkerName, dispatch.ModelName, dispatch.DispatchLane, dispatch.ModelSelectionReason);
    }

    private sealed record Route(string WorkerName, string? ModelName, string? DispatchLane, string? ModelSelectionReason);
}
