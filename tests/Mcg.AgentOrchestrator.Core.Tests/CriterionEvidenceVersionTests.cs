using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceVersionTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SupersededPendingEvidenceRemainsHistoricalWithoutBlockingSatisfiedCurrentSpec(bool restart)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe the authoritative requirement");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        var current = Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionVersion == 2);
        kernel.RecordCriterionEvidence(goal.Id, current.Id, CriterionEvidenceOwner.Operator,
            "candidate-b", "current-observation", current.RequiredScope, true, "Replacement behavior observed");
        if (restart)
        {
            var json = JsonSerializer.Serialize(kernel.ExportSnapshot());
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
            goal = kernel.GetGoal(goal.Id);
        }

        Assert.Empty(goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(old, Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == old.Id));
        Assert.Equal(CriterionEvidenceState.Satisfied,
            Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == current.Id).State);
    }

    [Xunit.Fact]
    public void LateHistoricalReceiptDoesNotSatisfyCurrentRequirement()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe the authoritative requirement");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        kernel.RecordCriterionEvidence(goal.Id, old.Id, CriterionEvidenceOwner.Operator,
            "candidate-a", "old-observation", old.RequiredScope, true, "Old behavior observed");

        var remaining = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(2, remaining.CriterionVersion);
        Assert.Equal(CriterionEvidenceState.Pending, remaining.State);
        Assert.Equal("Replacement observation", remaining.Criterion);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void SupersessionDoesNotHideMalformedOrUnrecognizedEvidence(bool malformed)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retain unresolved evidence identity");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.CriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        var current = Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionVersion == 2);
        var satisfied = kernel.RecordCriterionEvidence(goal.Id, current.Id, CriterionEvidenceOwner.Operator,
            "candidate-b", "current-observation", current.RequiredScope, true, "Replacement behavior observed");
        var unknown = malformed ? old with { Id = "" } : old with { CriterionVersion = 99 };
        var snapshot = kernel.ExportGoalSnapshot(goal.Id) with { CriterionEvidenceObligations = [unknown, satisfied] };

        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(goal.Id);

        var remaining = Assert.Single(restored.OutstandingCriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, remaining.State);
        if (malformed) Assert.Equal(CriterionEvidenceOwner.Unknown, remaining.Owner);
        else Assert.Equal(99, remaining.CriterionVersion);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void ExplicitRemappingRetiresUnresolvedPriorVersion(bool changeCount, bool restart)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateRevisedExplicitMapping(kernel, changeCount);
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 4, 2, CriterionEvidenceOwner.Acceptance,
            "operator", CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");
        kernel.RecordCriterionEvidence(goal.Id, mapped.Id, CriterionEvidenceOwner.Acceptance,
            "candidate-a", "full-gate-receipt", CriterionEvidenceScopes.FullAcceptanceGate, true, "Full gate passed");
        if (restart)
        {
            var json = JsonSerializer.Serialize(kernel.ExportSnapshot());
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
            goal = kernel.GetGoal(goal.Id);
        }

        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Empty(goal.OutstandingCriterionEvidenceObligations);
        var historical = Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == "criterion-v1-4");
        Assert.Equal(CriterionEvidenceOwner.Unknown, historical.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, historical.State);
        Assert.Contains("unresolved during refinement v2", historical.Provenance);
        Assert.Contains(mapped.Id, historical.Detail!);
        Assert.Equal(historical.Detail,
            Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == historical.Id).Detail);
        Assert.Null(Assert.Single(kernel.ExportGoalSnapshot(goal.Id).CriterionEvidenceObligations!,
            item => item.Id == historical.Id).Detail);
    }

    [Xunit.Fact]
    public void UnresolvedPriorVersionBlocksWithoutLaterMapping()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateRevisedExplicitMapping(kernel, changeCount: true);

        var outstanding = Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Equal("criterion-v1-4", outstanding.Id);
        Assert.Equal(CriterionEvidenceOwner.Unknown, outstanding.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, outstanding.State);
        Assert.Contains("unresolved during refinement v2", outstanding.Provenance);
        Assert.Null(outstanding.Detail);
        Assert.Null(Assert.Single(goal.CriterionEvidenceObligations).Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator)]
    public void RemappingRetiresHistoryWhileCurrentEvidenceStillBlocks(CriterionEvidenceOwner owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateRevisedExplicitMapping(kernel, changeCount: true);
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 4, 2, owner,
            "operator", owner == CriterionEvidenceOwner.Acceptance
                ? CriterionEvidenceScopes.FullAcceptanceGate : "operator observation", "mapping", "candidate-a");

        var outstanding = Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Equal(mapped.Id, outstanding.Id);
        Assert.Equal(CriterionEvidenceState.Pending, outstanding.State);
        Assert.Contains(mapped.Id,
            Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == "criterion-v1-4").Detail!);
    }

    [Xunit.Fact]
    public void LaterUnknownOwnershipDoesNotRetireUnresolvedHistory()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateRevisedExplicitMapping(kernel, changeCount: true);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var unresolved = Assert.Single(snapshot.CriterionEvidenceObligations!);
        var later = unresolved with
        {
            Id = "criterion-v2-4", CriterionVersion = 2, Criterion = "Rewritten criterion 5",
            Provenance = "ambiguous authoritative refined spec ownership"
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [snapshot with { CriterionEvidenceObligations = [unresolved, later] }], [])).GetGoal(goal.Id);

        Assert.Contains(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"),
            item => item.Id == unresolved.Id);
        Assert.Null(Assert.Single(restored.CriterionEvidenceObligations, item => item.Id == unresolved.Id).Detail);
    }

    private static Goal CreateRevisedExplicitMapping(AgentOrchestratorKernel kernel, bool changeCount)
    {
        var goal = kernel.CreateGoal("Retire an answered criterion ownership demand");
        string[] criteria = ["Criterion 1", "Criterion 2", "Criterion 3", "Criterion 4", "Original criterion 5"];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Observe current ownership", criteria, VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 4, 1, CriterionEvidenceOwner.Acceptance,
            "operator", CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");
        string[] revised = [.. criteria.Take(4), "Rewritten criterion 5"];
        if (changeCount) revised = [.. revised, "Criterion 6"];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Observe current ownership", revised, VerificationClass.TestVerifiable, [], []));
        var unresolved = Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == "criterion-v1-4");
        Assert.Equal(CriterionEvidenceOwner.Unknown, unresolved.Owner);
        Assert.Contains("unresolved during refinement v2", unresolved.Provenance);
        return goal;
    }

    private static RefinedSpec OwnedSpec(string criterion) => new(
        "Observe the authoritative behavior", [criterion], VerificationClass.RealWorldDependent, [], [])
    {
        OperatorOwnedAcceptanceCriteria = [criterion]
    };
}
