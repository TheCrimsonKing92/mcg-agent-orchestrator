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
    public void ResolveRoutesTestCoverageToDeveloperAndEvidenceToTester()
    {
        var coverageRoute = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestCoverage)],
            "test source change");
        var evidenceRoute = ReviewFindingRouting.Resolve(
            [Finding(FindingCategory.TestEvidence)],
            "focused receipt");

        Assert.Equal(AgentRole.Developer, coverageRoute.TargetRole);
        Assert.Equal(AgentRole.Tester, evidenceRoute.TargetRole);
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

    [Xunit.Fact]
    public void ResolveRoutesWritableFindingBeforeNonWorkerObligations()
    {
        var route = ReviewFindingRouting.Resolve(
            [
                Finding(FindingCategory.OperatorOwned),
                Finding(FindingCategory.AcceptanceOwned),
                Finding(FindingCategory.Correctness)
            ],
            "mixed obligations");

        Assert.False(route.EscalateToOperator);
        Assert.Equal(AgentRole.Developer, route.TargetRole);
    }

    [Xunit.Fact]
    public void ProjectPreservesPerFindingOwnersAndStableAnchors()
    {
        var developer = Finding(FindingCategory.Correctness) with
        {
            StableId = "source-defect",
            Location = new ReviewFindingLocation("src/Current.cs", "Current.Run")
        };
        var acceptance = Finding(FindingCategory.AcceptanceOwned) with
        {
            StableId = "focused-green",
            Location = new ReviewFindingLocation("tests/CurrentTests.cs", "CurrentTests.Run")
        };

        var projections = ReviewFindingRouting.Project([developer, acceptance]);

        var writable = Assert.Single(projections, item => item.TargetRole == AgentRole.Developer);
        Assert.Equal("source-defect", writable.Finding.StableId);
        Assert.Equal("src/Current.cs", writable.Finding.Location.File);
        var acceptanceOwned = Assert.Single(projections, item => item.AcceptanceOwned);
        Assert.Equal("focused-green", acceptanceOwned.Finding.StableId);
        Assert.Null(acceptanceOwned.TargetRole);
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
