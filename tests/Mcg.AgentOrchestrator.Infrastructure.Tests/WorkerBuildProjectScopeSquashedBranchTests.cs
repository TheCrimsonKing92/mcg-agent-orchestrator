using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its repository and captures diagnostics without changing Console.Error.
public sealed class WorkerBuildProjectScopeSquashedBranchTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SquashedDispatchBaseIsReplacedAtNextDispatch(bool pinBaseline)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var previous = PrepareThenSquash(fixture);
        var head = fixture.Git("rev-parse", "HEAD").Trim();
        var diagnostics = new List<string>();

        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "round-2", "landing",
            pinnedGoalBaseline: pinBaseline ? previous.GoalBaseline : null, diagnostic: diagnostics.Add);

        var replacement = fixture.Metadata();
        Assert.Equal(head, replacement.DispatchBase);
        Assert.Equal(previous.GoalBaseline, replacement.GoalBaseline);
        Assert.Equal(previous.GoalId, replacement.GoalId);
        Assert.Equal(previous.Branch, replacement.Branch);
        Assert.Equal(previous.IntegrationBranch, replacement.IntegrationBranch);
        Assert.NotEqual(previous.DispatchIdentity, replacement.DispatchIdentity);
        Assert.Equal("round-2", replacement.TaskId);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains(previous.DispatchBase, diagnostic, StringComparison.Ordinal);
        Assert.Contains(head, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', diagnostic);
        Assert.DoesNotContain('\r', diagnostic);
        Assert.Equal(previous.GoalBaseline, WorkerBuildProjectScope.ReadDispatchIdentity(fixture.Root, head).GoalBaseline);
    }

    [Xunit.Fact]
    public void RewrittenGoalBaselineStillFailsClosed()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var previous = PrepareThenSquash(fixture);
        fixture.Git("checkout", "--orphan", "foreign");
        fixture.Write(".foreign-history", "unrelated root");
        fixture.Commit();
        fixture.Git("checkout", "goal/" + fixture.Prefix);
        fixture.Git("reset", "--hard", "foreign");
        AssertAncestry(fixture, previous.GoalBaseline, 1);
        var diagnostics = new List<string>();

        var error = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Prepare(
            fixture.Root, fixture.GoalId, "round-2", "landing", diagnostic: diagnostics.Add));

        Assert.Contains("Git merge-base failed (exit=1)", error.Message, StringComparison.Ordinal);
        Assert.Empty(diagnostics);
        Assert.Equal(previous, fixture.Metadata());
    }

    [Xunit.Fact]
    public void AncestorDispatchBaseIsReusedUnchanged()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        fixture.Change("Core");
        fixture.Commit();
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "round-1", "landing");
        var previous = fixture.Metadata();
        AssertAncestry(fixture, previous.DispatchBase, 0);
        var diagnostics = new List<string>();

        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "round-2", "landing", diagnostic: diagnostics.Add);

        Assert.Equal(previous.DispatchBase, fixture.Metadata().DispatchBase);
        Assert.Equal(previous.GoalBaseline, fixture.Metadata().GoalBaseline);
        Assert.Empty(diagnostics);
    }

    [Xunit.Theory]
    [Xunit.InlineData("unknown")]
    [Xunit.InlineData("malformed")]
    public void InvalidDispatchBaseStillFailsClosed(string scenario)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var revision = scenario == "unknown" ? new string('a', 40) : "--not-a-revision";
        fixture.SetMetadata(fixture.Metadata() with { DispatchBase = revision });
        var previous = fixture.Metadata();
        if (scenario == "unknown")
        {
            var ancestry = GitCli.RunForRepository(fixture.Root, "merge-base", "--is-ancestor", revision, "HEAD");
            Assert.False(ancestry.DrainTimedOut);
            Assert.Equal(128, ancestry.ExitCode);
        }
        var diagnostics = new List<string>();

        var error = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Prepare(
            fixture.Root, fixture.GoalId, "round-2", "landing", diagnostic: diagnostics.Add));

        Assert.Contains(scenario == "unknown" ? "Git merge-base failed (exit=128)" : "requires immutable commit identities",
            error.Message, StringComparison.Ordinal);
        Assert.Empty(diagnostics);
        Assert.Equal(previous, fixture.Metadata());
    }

    [Xunit.Theory]
    [Xunit.InlineData("goal")]
    [Xunit.InlineData("integration")]
    [Xunit.InlineData("pinned-baseline")]
    [Xunit.InlineData("root")]
    [Xunit.InlineData("branch")]
    [Xunit.InlineData("version")]
    public void StaleDispatchBaseCannotBypassExistingGuards(string scenario)
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var previous = PrepareThenSquash(fixture);
        previous = scenario switch
        {
            "root" => previous with { WorktreeRoot = Path.GetDirectoryName(fixture.Root)! },
            "branch" => previous with { Branch = "goal/foreign" },
            "version" => previous with { Version = 2 },
            _ => previous
        };
        fixture.SetMetadata(previous);
        var diagnostics = new List<string>();

        var error = Assert.Throws<IOException>(() => WorkerBuildProjectScope.Prepare(fixture.Root,
            scenario == "goal" ? fixture.GoalId + "other" : fixture.GoalId, "round-2",
            scenario == "integration" ? "other-landing" : "landing",
            pinnedGoalBaseline: scenario == "pinned-baseline" ? fixture.Git("rev-parse", "HEAD").Trim() : null,
            diagnostic: diagnostics.Add));

        var expected = scenario switch
        {
            "goal" or "integration" => "Worker build scope goal/integration identity changed; conductor must replace the scope explicitly.",
            "pinned-baseline" => "Worker build scope baseline differs from the conductor's pinned goal identity.",
            _ => "Foreign or malformed worker build scope metadata."
        };
        Assert.Equal(expected, error.Message);
        Assert.Empty(diagnostics);
        Assert.Equal(previous, fixture.Metadata());
    }

    private static WorkerBuildProjectScope.Metadata PrepareThenSquash(WorkerBuildProjectScopeTests.Fixture fixture)
    {
        var baseline = fixture.Metadata().GoalBaseline;
        fixture.Change("Core");
        fixture.Commit(); // B, on top of fixture baseline A.
        fixture.Change("App");
        fixture.Commit(); // C, the dispatch base that the squash removes.
        var dispatchBase = fixture.Git("rev-parse", "HEAD").Trim();
        WorkerBuildProjectScope.Prepare(fixture.Root, fixture.GoalId, "round-1", "landing");
        var previous = fixture.Metadata();
        Assert.Equal(baseline, previous.GoalBaseline);
        Assert.Equal(dispatchBase, previous.DispatchBase);

        fixture.Git("reset", "--soft", baseline);
        fixture.Commit(); // S has A as its only parent and the combined B/C tree.
        Assert.Equal(baseline, fixture.Git("rev-parse", "HEAD^").Trim());
        AssertAncestry(fixture, previous.GoalBaseline, 0);
        AssertAncestry(fixture, previous.DispatchBase, 1);
        return previous;
    }

    private static void AssertAncestry(WorkerBuildProjectScopeTests.Fixture fixture, string revision, int expectedExit)
    {
        var result = GitCli.RunForRepository(fixture.Root, "merge-base", "--is-ancestor", revision, "HEAD");
        Assert.False(result.DrainTimedOut);
        Assert.Equal(expectedExit, result.ExitCode);
    }
}
