using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: evidence-request creation uses an isolated declared-project manifest.
public sealed class DeclaredTestEvidenceSelectorsTests
{
    private const string Declaration = "tests: deferred - version_flag_prints_package_version,tests::no_arguments_prints_usage_and_exits_2";

    [Fact]
    public void DeveloperNames_WithReport_UseCanonicalIdentities()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var selectors = DeclaredTestEvidenceSelectors.FromDeveloperReport(Declaration, fixture.Read().Rows);
        Assert.Equal(new[] { DeclaredTestReplayFixture.Version, DeclaredTestReplayFixture.Usage }, selectors);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, fixture.Evaluate(selectors.ToArray()).Outcome);
    }

    [Fact]
    public void EvidenceRequest_BeforeReport_PreservesLiteralDeveloperNames()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var selection = DeveloperDeferredTestSelections.Resolve(fixture.Gate.Root, Declaration);
        Assert.Empty(selection.NotRun);
        Assert.Equal(new[] { "version_flag_prints_package_version", "tests::no_arguments_prints_usage_and_exits_2" },
            selection.Selections.Select(item => item.TestClass));
        Assert.All(selection.Selections, item => Assert.Equal(".", item.TestProject));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied,
            fixture.Evaluate(selection.Selections.Select(item => item.TestClass).ToArray()).Outcome);
    }

    [Fact]
    public void AmbiguousReportedName_StaysLiteral()
    {
        var rows = new[] {
            new DeclaredTestReportRows.Row("a::case", "case", ["a"], "Passed", "a"),
            new DeclaredTestReportRows.Row("b::case", "case", ["b"], "Passed", "b") };
        Assert.Equal(new[] { "case" }, DeclaredTestEvidenceSelectors.FromDeveloperReport("deferred - case", rows));
    }
}
