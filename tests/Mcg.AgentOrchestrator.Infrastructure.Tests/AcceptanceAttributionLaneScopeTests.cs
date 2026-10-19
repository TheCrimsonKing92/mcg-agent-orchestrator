using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class AcceptanceAttributionLaneScopeTests
{
    [Theory]
    [InlineData(new[] { AttributionLaneFixture.Process, AttributionLaneFixture.Remainder, "infrastructure tests" }, new[] { AttributionLaneFixture.Process, AttributionLaneFixture.Remainder })]
    [InlineData(new[] { AttributionLaneFixture.Remainder, "infrastructure lane early stop", "infrastructure tests" }, new[] { AttributionLaneFixture.Remainder })]
    [InlineData(new[] { AttributionLaneFixture.Remainder, "cli tests" }, new[] { "cli tests", AttributionLaneFixture.Remainder })]
    public void FailedChecks_SelectExactlyTheDispositiveNames(string[] failures, string[] expected)
    {
        var scope = Assert.IsType<AcceptanceAttributionLaneScope>(AcceptanceAttributionLaneScope.FromCombinedFailedChecks(failures));
        Assert.Equal(expected, scope.Names);
    }

    [Fact]
    public void WithoutInfrastructureLane_LeavesScopeAbsent()
    {
        Assert.Null(AcceptanceAttributionLaneScope.FromCombinedFailedChecks(["cli tests"]));
        Assert.Null(AcceptanceAttributionLaneScope.FromCombinedFailedChecks([]));
    }

    [Fact]
    public void Apply_KeepsOnlyFailedLanesInPlanOrderAndDisablesStructuralCoverage()
    {
        var plan = AttributionLaneFixture.Plan.Concat(new[] { "core tests", "cli tests", "git diff whitespace" }.Select(CheckNamed)).ToArray();
        Assert.Equal(27, plan.Length);
        using var log = new StringWriter();
        var scope = AcceptanceAttributionLaneScope.FromCombinedFailedChecks([AttributionLaneFixture.Process, AttributionLaneFixture.Remainder]);
        var result = AcceptanceAttributionLaneScope.Apply(plan, [], true, scope, log);
        Assert.Equal(new[] { AttributionLaneFixture.Remainder, AttributionLaneFixture.Process }, result.Checks.Select(check => check.Name));
        Assert.False(result.StructuralCoverageApplies);
        Assert.Contains("ATTRIBUTION_LANE_SCOPE status=applied checks=2 names=infrastructure tests: Remainder,infrastructure tests: Process spawning", log.ToString());
    }

    [Fact]
    public void Apply_NullAndUnresolvedScopesPreserveFullPlanAndFlag()
    {
        var plan = AttributionLaneFixture.Plan.Concat(new[] { "core tests", "cli tests", "git diff whitespace" }.Select(CheckNamed)).ToArray();
        using var log = new StringWriter();
        foreach (var flag in new[] { false, true })
        {
            var unchanged = AcceptanceAttributionLaneScope.Apply(plan, [], flag, null, log);
            Assert.Same(plan, unchanged.Checks);
            Assert.Equal(flag, unchanged.StructuralCoverageApplies);
        }
        Assert.Empty(log.ToString());
        var scope = AcceptanceAttributionLaneScope.FromCombinedFailedChecks(["infrastructure tests: Missing Z", "infrastructure tests: Missing A"]);
        var unresolved = AcceptanceAttributionLaneScope.Apply(plan, [], true, scope, log);
        Assert.Same(plan, unresolved.Checks);
        Assert.Equal(27, unresolved.Checks.Count);
        Assert.True(unresolved.StructuralCoverageApplies);
        Assert.Equal($"ATTRIBUTION_LANE_SCOPE status=unresolved name=infrastructure tests: Missing A fallback=full-plan{Environment.NewLine}", log.ToString());
    }

    [Fact]
    public void Apply_AddsMissingCandidateLaneAndPreservesExplicitStructuralCoverage()
    {
        Check[] plan = [CheckNamed(AttributionLaneFixture.Remainder), CheckNamed("structural test coverage")];
        Check[] candidates = [CheckNamed(AttributionLaneFixture.Process)];
        var scope = AcceptanceAttributionLaneScope.FromCombinedFailedChecks([AttributionLaneFixture.Process, "structural test coverage", AttributionLaneFixture.Remainder]);
        var result = AcceptanceAttributionLaneScope.Apply(plan, candidates, true, scope, TextWriter.Null);
        Assert.Equal(new[] { AttributionLaneFixture.Remainder, "structural test coverage", AttributionLaneFixture.Process }, result.Checks.Select(check => check.Name));
        Assert.Same(candidates[0], result.Checks[2]);
        Assert.True(result.StructuralCoverageApplies);
        Assert.False(AcceptanceAttributionLaneScope.Apply(plan, candidates, false, scope, TextWriter.Null).StructuralCoverageApplies);
    }

    [Fact]
    public void Hash_IsOrdinalDistinctSortedUtf8AndMatchingIsExact()
    {
        var scope = AcceptanceAttributionLaneScope.FromCombinedFailedChecks([AttributionLaneFixture.Remainder, "cli tests", AttributionLaneFixture.Remainder])!;
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("cli tests\ninfrastructure tests: Remainder")));
        Assert.Equal(expected, scope.Sha256);
        Assert.Equal(scope.Sha256, AcceptanceAttributionLaneScope.FromCombinedFailedChecks(["cli tests", AttributionLaneFixture.Remainder])!.Sha256);
        Check[] wrongCase = [CheckNamed(AttributionLaneFixture.Remainder.ToUpperInvariant()), CheckNamed("cli tests")];
        Assert.Same(wrongCase, AcceptanceAttributionLaneScope.Apply(wrongCase, [], false, scope, TextWriter.Null).Checks);
    }

    private static Check CheckNamed(string name) => new() { Name = name };
}

internal static class AttributionLaneFixture
{
    internal const string Remainder = "infrastructure tests: Remainder";
    internal const string Process = "infrastructure tests: Process spawning";
    internal static readonly (string Name, int Seconds)[] Lanes =
    [
        (Remainder, 1834), (Process, 891),
        ("infrastructure tests: Process spawning parallel", 737),
        ("infrastructure tests: Goal acceptance build slots", 610),
        ("infrastructure tests: Goal worktree cleanup cohort", 554),
        ("infrastructure tests: Goal lifecycle commands", 468),
        ("infrastructure tests: Dotnet build slots", 328),
        ("infrastructure tests: Goal worktree cleanup git", 279),
        ("infrastructure tests: Worker dispatch fixtures A", 254),
        ("infrastructure tests: Process spawning process-local", 217),
        ("infrastructure tests: Process spawning host-bound", 207),
        ("infrastructure tests: Remainder merge train and ratchet", 195),
        ("infrastructure tests: Worker dispatch fixtures C", 190),
        ("infrastructure tests: Cli", 130), ("infrastructure tests: Chaos gate", 99),
        ("infrastructure tests: Worker dispatch fixtures B", 89),
        ("infrastructure tests: Goal worktree parallel", 81),
        ("infrastructure tests: Worker profiles", 78),
        ("infrastructure tests: Goal acceptance verifier", 62),
        ("infrastructure tests: Dotnet build slots local-only", 29),
        ("infrastructure tests: Remainder balance A", 23),
        ("infrastructure tests: Remainder balance B", 20),
        ("infrastructure tests: Conduct watch sweep scoping", 10),
        ("infrastructure tests: Storage retention", 7)
    ];
    internal static Check[] Plan => Lanes.Select(lane => new Check { Name = lane.Name }).ToArray();
}
