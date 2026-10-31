using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: no shared stores or process state.
public sealed class DeclaredTestSelectorNotFoundDiagnosticTests
{
    [Theory]
    [InlineData("conduct-tail::versoin")]
    [InlineData("conduct-tail::vers")]
    [InlineData("Conduct-tail::version")]
    public void UnmatchedSelector_NamesClosestReportIdentities(string selector)
    {
        using var fixture = new DeclaredTestReplayFixture();
        var result = fixture.Evaluate(selector);
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { selector }, result.Selectors);
        Assert.Equal(new[] { DeclaredTestReplayFixture.Version, DeclaredTestReplayFixture.Usage }, result.ClosestIdentities);
    }

    [Fact]
    public void ClosestIdentities_AreDistinctBoundedAndOrdinalOnTies()
    {
        var rows = new[] { "x::d", "x::c", "x::b", "x::a", "x::a" }
            .Select(name => new DeclaredTestReportRows.Row(name, name, [], "Passed", "report")).ToArray();
        Assert.Equal(new[] { "x::a", "x::b", "x::c" }, DeclaredTestClosestIdentities.Find(["x::z"], rows));
    }

    [Fact]
    public void LandingHold_DisplaysClosestReportIdentities()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var gate = fixture.Gate;
        var kernel = GateTestObligationIntakeTests.Seed(gate.Kernel, gate.Goal, ["conduct-tail::versoin"]);
        var workspace = new OrchestratorWorkspace(gate.Root, gate.Root, gate.OrchestratorDirectory,
            "declared", false, "default", false, "", "", "", "", "", "", "");
        var result = LandingExecutor.EvaluatePendingHumanInput(kernel, kernel.GetGoal(gate.Goal.Id),
            workspace, gate.CandidateSha);
        var line = Assert.Single(result.ObligationLines);
        Assert.Contains("selector-not-found: conduct-tail::versoin", line);
        Assert.Contains("; closest: " + DeclaredTestReplayFixture.Version + ", " + DeclaredTestReplayFixture.Usage, line);
        Assert.Contains(line, result.HoldDescription!);
    }
}
