using Mcg.AgentOrchestrator.Core;

public sealed class ReviewFindingRoutingTests
{
    [Xunit.Fact]
    public void ResolveEscalatesOperatorOwnedAndSpecDefectBeforeOtherCategories()
    {
        var route = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestEvidence), Finding(FindingCategory.SpecDefect)],
            "tester");

        Assert.True(route.EscalateToOperator);
        Assert.Null(route.TargetRole);
    }

    [Xunit.Fact]
    public void ResolveRoutesOnlyTestCategoriesToTester()
    {
        var route = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestEvidence), Finding(FindingCategory.TestCoverage)],
            "source change");

        Assert.False(route.EscalateToOperator);
        Assert.Equal(AgentRole.Tester, route.TargetRole);
    }

    [Xunit.Fact]
    public void ResolveRoutesMixedTestAndSourceCategoriesToDeveloper()
    {
        var route = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestEvidence), Finding(FindingCategory.Correctness)],
            "tester");

        Assert.Equal(AgentRole.Developer, route.TargetRole);
    }

    [Xunit.Fact]
    public void ResolveRoutesMixedTypedAndUnspecifiedFindingsToDeveloper()
    {
        var route = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestEvidence), Finding(FindingCategory.Unspecified)],
            "tester");

        Assert.Equal(AgentRole.Developer, route.TargetRole);
    }

    [Xunit.Theory]
    [Xunit.InlineData("tester must rerun", AgentRole.Tester, false)]
    [Xunit.InlineData("missing test receipt", AgentRole.Tester, false)]
    [Xunit.InlineData("run verification command", AgentRole.Tester, false)]
    [Xunit.InlineData("operator-owned evidence", null, true)]
    [Xunit.InlineData("human input required", null, true)]
    [Xunit.InlineData("source guard missing", AgentRole.Developer, false)]
    public void ResolvePreservesLegacyProseFallback(
        string prose,
        AgentRole? expectedRole,
        bool expectedEscalation)
    {
        var route = ReviewFindingRouting.Resolve([Finding(FindingCategory.Unspecified)], prose);

        Assert.Equal(expectedRole, route.TargetRole);
        Assert.Equal(expectedEscalation, route.EscalateToOperator);
    }

    private static ReviewFinding Finding(FindingCategory category) =>
        new(
            Guid.NewGuid().ToString("N"),
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run"),
            "Finding.",
            FindingSeverity.Blocking,
            category);
}
