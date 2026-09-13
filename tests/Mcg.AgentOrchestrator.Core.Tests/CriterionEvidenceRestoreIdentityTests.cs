using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceRestoreIdentityTests
{
    [Xunit.Theory]
    [Xunit.InlineData("id")]
    [Xunit.InlineData("index")]
    [Xunit.InlineData("version")]
    [Xunit.InlineData("criterion")]
    [Xunit.InlineData("owner")]
    [Xunit.InlineData("worker-owner")]
    [Xunit.InlineData("unknown-owner")]
    [Xunit.InlineData("state")]
    [Xunit.InlineData("receipt")]
    [Xunit.InlineData("detail")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("acceptance-unbound")]
    [Xunit.InlineData("acceptance-scope")]
    [Xunit.InlineData("history-null")]
    [Xunit.InlineData("history-duplicate")]
    [Xunit.InlineData("history-current-id")]
    public void MalformedPersistedEvidenceCannotSatisfyTheGoal(string alteredField)
    {
        var (kernel, goal, evidence) = CreateEvidence(CriterionEvidenceOwner.Operator);
        var previous = new CriterionEvidenceReceipt(evidence.Owner, "candidate-previous", "previous-receipt",
            evidence.RequiredScope, true, "Earlier observation", evidence.Provenance, evidence.RecordedAt);
        var malformed = alteredField switch
        {
            "id" => evidence with { Id = "criterion-v1-42" },
            "index" => evidence with { CriterionIndex = 42 },
            "version" => evidence with { CriterionVersion = 99 },
            "criterion" => evidence with { Criterion = "Different requirement" },
            "owner" => evidence with { Owner = (CriterionEvidenceOwner)99 },
            "worker-owner" => evidence with { Owner = CriterionEvidenceOwner.Worker },
            "unknown-owner" => evidence with { Owner = CriterionEvidenceOwner.Unknown },
            "state" => evidence with { State = (CriterionEvidenceState)99 },
            "receipt" => evidence with { ReceiptId = null },
            "detail" => evidence with { Detail = null },
            "candidate" => evidence with { CandidateSha = null },
            "acceptance-unbound" => evidence with { Owner = CriterionEvidenceOwner.Acceptance,
                RequiredScope = CriterionEvidenceScopes.FullAcceptanceGate, ExpectedCandidateSha = null },
            "acceptance-scope" => evidence with { Owner = CriterionEvidenceOwner.Acceptance },
            "history-null" => evidence with { PriorReceipts = [null!] },
            "history-duplicate" => evidence with { PriorReceipts = [previous, previous] },
            "history-current-id" => evidence with { PriorReceipts = [previous with { ReceiptId = evidence.ReceiptId! }] },
            _ => throw new ArgumentOutOfRangeException(nameof(alteredField))
        };
        var snapshot = kernel.ExportGoalSnapshot(goal.Id) with { CriterionEvidenceObligations = [malformed] };

        var restored = Restore(snapshot);

        var blocked = Assert.Single(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Equal(CriterionEvidenceOwner.Unknown, blocked.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, blocked.State);
        Assert.False(blocked.HasSatisfiedEvidenceFor("candidate-a"));
        Assert.Equal(JsonSerializer.Serialize(restored.CriterionEvidenceObligations),
            JsonSerializer.Serialize(Restore(restored.ToSnapshot()).CriterionEvidenceObligations));
    }

    [Xunit.Fact]
    public void NullPersistedObligationRemainsAnUnresolvedDiagnosticAcrossRestart()
    {
        var (kernel, goal, _) = CreateEvidence(CriterionEvidenceOwner.Operator);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id) with { CriterionEvidenceObligations = [null!] };

        var restored = Restore(snapshot);

        var unknown = Assert.Single(restored.OutstandingCriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Unknown, unknown.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, unknown.State);
        Assert.Equal(unknown, Assert.Single(Restore(restored.ToSnapshot()).OutstandingCriterionEvidenceObligations));
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance)]
    public void ValidPersistedEvidenceRetainsIdentityAndSatisfaction(CriterionEvidenceOwner owner)
    {
        var (kernel, goal, evidence) = CreateEvidence(owner);
        var restored = Restore(kernel.ExportGoalSnapshot(goal.Id));

        Assert.Equal(evidence, Assert.Single(restored.CriterionEvidenceObligations));
        Assert.Empty(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Worker)]
    [Xunit.InlineData(CriterionEvidenceOwner.Unknown)]
    [Xunit.InlineData((CriterionEvidenceOwner)99)]
    public void PublicMappingRejectsOwnersWithoutEvidenceAuthority(CriterionEvidenceOwner owner)
    {
        var (kernel, goal, evidence) = CreateEvidence(CriterionEvidenceOwner.Operator);

        Assert.Throws<ArgumentOutOfRangeException>(() => kernel.MapCriterionEvidenceOwner(
            goal.Id, 0, 1, owner, "operator", expectedCandidateSha: "candidate-b"));
        Assert.Equal(evidence, Assert.Single(goal.CriterionEvidenceObligations));
    }

    [Xunit.Fact]
    public void UnknownOwnershipCannotBeSatisfiedByAnUnknownReceipt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Resolve ambiguous ownership before evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Behavior observed"],
            VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = ["Behavior observed", "Behavior observed"]
        });
        var unknown = Assert.Single(goal.CriterionEvidenceObligations);

        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, unknown.Id,
            CriterionEvidenceOwner.Unknown, "candidate-a", "unknown-proof", unknown.RequiredScope, true, "Claimed pass"));
        Assert.Equal(unknown, Assert.Single(goal.OutstandingCriterionEvidenceObligations));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, CriterionEvidenceObligation Evidence) CreateEvidence(
        CriterionEvidenceOwner owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep persisted evidence tied to its requirement");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Behavior observed"],
            VerificationClass.RealWorldDependent, [], []));
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator", expectedCandidateSha: "candidate-a");
        var evidence = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner, "candidate-a", "observation-a",
            mapped.RequiredScope, true, "Observed the required behavior");
        return (kernel, goal, evidence);
    }

    private static Goal Restore(GoalSnapshot snapshot) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(new GoalId(snapshot.Id));
}
