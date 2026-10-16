using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class FindingEvidenceRoundPlannerTests
{
    [Xunit.Fact]
    public void IndependentProjects_KeepFailFastBatches()
    {
        var infrastructure = Group("infrastructure", new FindingEvidenceSelection("Infrastructure.Tests", "RunnerTests"));
        var core = Group("core", new FindingEvidenceSelection("Core.Tests", "StateTests"));

        var batches = FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([infrastructure, core]);

        Assert.Equal(2, batches.Count);
        Assert.Equal("Infrastructure.Tests:RunnerTests", batches[0].Request);
        Assert.Equal("Core.Tests:StateTests", batches[1].Request);
    }

    [Xunit.Fact]
    public void OverlappingUnions_ExecuteSharedProjectSelectionOnce()
    {
        var first = Group("first", new("Core.Tests", "StateTests"), new("Infrastructure.Tests", "RunnerTests"));
        var second = Group("second", new("Infrastructure.Tests", "FullyQualifiedName~RunnerTests"),
            new("Dashboard.Tests", "RenderTests"));

        var batch = Assert.Single(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([first, second]));

        Assert.Equal("Core.Tests:StateTests; Dashboard.Tests:RenderTests; Infrastructure.Tests:RunnerTests", batch.Request);
        Assert.Equal(3, batch.TypedRequest.Selections.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ExplicitUnion_CanonicalizesAlternativesAndOwnsMemberDecisions(bool unionFirst)
    {
        var core = Group("core", new FindingEvidenceSelection("Core.Tests", " (FullyQualifiedName~StateTests|FullyQualifiedName~RunnerTests) "));
        var dashboard = Group("dashboard", new FindingEvidenceSelection("Dashboard.Tests", "RenderTests"));
        var union = Group("union", new("Dashboard.Tests", "RenderTests"),
            new("Core.Tests", "RunnerTests"), new("Core.Tests", "StateTests"));
        var batches = FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches(
            unionFirst ? [union, dashboard, core] : [core, dashboard, union]);

        var batch = Assert.Single(batches);
        Assert.Equal("Core.Tests:RunnerTests; Core.Tests:StateTests; Dashboard.Tests:RenderTests", batch.Request);
        var decisions = FindingEvidenceRoundPlanner.BuildInitialRequestDispositions(
            batches, batch, "548214df", "finding-round-fixture", "basis");
        Assert.Equal(3, decisions.Count);
        Assert.Equal(FindingEvidenceRequestDecision.PendingExecution,
            decisions.Single(decision => decision.FindingStableId == "union").Decision);
        Assert.All(decisions.Where(decision => decision.FindingStableId != "union"), decision =>
        {
            Assert.Equal(FindingEvidenceRequestDecision.CoveredByPendingRequest, decision.Decision);
            Assert.Equal("union", decision.CoveringRequestIdentity);
            Assert.Empty(decision.SourceReceiptIds ?? []);
        });
    }

    private static FindingEvidenceRequestGroup Group(string id, params FindingEvidenceSelection[] selections) =>
        new(id, string.Join("; ", selections.Select(selection => selection.TestProject + ":" + selection.TestClass)),
            new(selections), [new(id, ReviewFindingState.Open, new("tests/fixture.cs", id), "Receipt missing.")]);
}
