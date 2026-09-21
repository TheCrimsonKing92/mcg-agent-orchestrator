using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class WaiveDeferredCriterionDispositionTests
{
    [Xunit.Fact]
    public void WaiveWithoutDispositionIsRefusedWhenAnotherCriterionHasOutstandingEvidence()
    {
        var (kernel, goal) = CreateGoalWithOutstandingCriteria(1);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(goal.Id, "1", "Mechanism moved to separate work."));

        Xunit.Assert.Contains("Measure the mechanism after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
    }

    [Xunit.Fact]
    public void OperatorOwnedCriterionSeedsObligationAndRequiresWaiverDisposition()
    {
        const string implementationCriterion = "Implement the mechanism.";
        const string operatorCriterion =
            "Measure the mechanism after landing. REAL-WORLD-DEPENDENT, operator-owned.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect the operator observation");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Require the live observation.",
            [implementationCriterion, operatorCriterion],
            VerificationClass.TestVerifiable,
            [],
            [])
        {
            OperatorOwnedAcceptanceCriteria = [operatorCriterion]
        });
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        Xunit.Assert.Equal(operatorCriterion, obligation.Criterion);
        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(goal.Id, "1", "Mechanism moved to separate work."));

        Xunit.Assert.Contains(operatorCriterion, error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);

        var waiver = kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "Mechanism moved to separate work.",
            dispositions: [new CriterionDispositionRequest("2", "Observation remains required.")]);
        Xunit.Assert.Equal("Observation remains required.", Xunit.Assert.Single(waiver.Dispositions!).Disposition);
    }

    [Xunit.Fact]
    public void PartialDispositionSupplyNamesOnlyMissingAffectedCriterion()
    {
        var (kernel, goal) = CreateGoalWithOutstandingCriteria(1, 2);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(
                goal.Id,
                "1",
                "Mechanism moved to separate work.",
                dispositions:
                [
                    new CriterionDispositionRequest("2", "Criterion 2 remains meaningful.")
                ]));

        Xunit.Assert.Contains("Observe the deployment after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Measure the mechanism after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
    }

    [Xunit.Fact]
    public void WaiveWithoutAffectedCriteriaKeepsLegacyShape()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel, ["Implement the mechanism."]);

        var waiver = kernel.WaiveAcceptanceCriterion(goal.Id, "1", "Requirement moved.");
        var snapshot = kernel.ExportSnapshot();

        Xunit.Assert.Null(waiver.Dispositions);
        Xunit.Assert.Null(Xunit.Assert.Single(snapshot.Goals)
            .EffectiveAcceptanceCriteriaCorrections![0].Dispositions);
        Xunit.Assert.DoesNotContain("\"Dispositions\"", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SatisfiedObligationForCurrentCandidateDoesNotRequireDisposition()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateGoal(kernel, ["Implement the mechanism.", "Measure the mechanism after landing."]);
        var obligation = MapOutstanding(kernel, goal, 1);
        kernel.RecordCriterionEvidence(
            goal.Id,
            obligation.Id,
            obligation.Owner,
            "candidate-a",
            "receipt-a",
            obligation.RequiredScope,
            passed: true,
            "Measured successfully.");
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var waiver = kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "Requirement moved.",
            currentCandidateSha: "candidate-a");

        Xunit.Assert.Null(waiver.Dispositions);
    }

    [Xunit.Fact]
    public void SatisfiedObligationForStaleCandidateRequiresDisposition()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateGoal(kernel, ["Implement the mechanism.", "Measure the mechanism after landing."]);
        var obligation = MapOutstanding(kernel, goal, 1);
        kernel.RecordCriterionEvidence(
            goal.Id,
            obligation.Id,
            obligation.Owner,
            "candidate-a",
            "receipt-a",
            obligation.RequiredScope,
            passed: true,
            "Measured successfully.");
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(
                goal.Id,
                "1",
                "Requirement moved.",
                currentCandidateSha: "candidate-b"));

        Xunit.Assert.Contains("Measure the mechanism after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
    }

    [Xunit.Fact]
    public void DispositionsPersistOnWaiverAndRoundTripWithWaiverProvenance()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = CreateGoal(kernel, ["Implement the mechanism.", "Measure the mechanism after landing."]);
        MapOutstanding(kernel, goal, 1);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var waiver = kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "Mechanism moved to separate work.",
            "operator@example",
            [new CriterionDispositionRequest("2", "Threshold is no longer applicable.")]);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock).GetGoal(goal.Id);
        var restoredWaiver = Xunit.Assert.Single(restored.EffectiveAcceptanceCriteriaCorrections);

        Xunit.Assert.Equal("operator@example", waiver.Actor);
        Xunit.Assert.Equal(clock.UtcNow, waiver.RecordedAt);
        Xunit.Assert.Equal(waiver.Actor, restoredWaiver.Actor);
        Xunit.Assert.Equal(waiver.RecordedAt, restoredWaiver.RecordedAt);
        var disposition = Xunit.Assert.Single(restoredWaiver.Dispositions!);
        Xunit.Assert.Equal("Measure the mechanism after landing.", disposition.Criterion);
        Xunit.Assert.Equal("Threshold is no longer applicable.", disposition.Disposition);
    }

    [Xunit.Fact]
    public void DispositionForNonAffectedCriterionIsRejected()
    {
        var (kernel, goal) = CreateGoalWithOutstandingCriteria(1);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(
                goal.Id,
                "1",
                "Requirement moved.",
                dispositions: [new CriterionDispositionRequest("1", "Typo target.")]));

        Xunit.Assert.Contains("not an affected outstanding criterion", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
    }

    [Xunit.Fact]
    public void DuplicateDispositionForSameAffectedCriterionIsRejected()
    {
        var (kernel, goal) = CreateGoalWithOutstandingCriteria(1);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(
                goal.Id,
                "1",
                "Requirement moved.",
                dispositions:
                [
                    new CriterionDispositionRequest("2", "First note."),
                    new CriterionDispositionRequest("Measure the mechanism after landing.", "Second note.")
                ]));

        Xunit.Assert.Contains("exactly one disposition", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
    }

    [Xunit.Fact]
    public void SecondWaiveRequiresFreshDispositionAndRetainsEachWaiverRecord()
    {
        var (kernel, goal) = CreateGoalWithOutstandingCriteria(2);
        kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "First premise removed.",
            dispositions: [new CriterionDispositionRequest("3", "First waiver leaves observation meaningful.")]);

        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.WaiveAcceptanceCriterion(goal.Id, "2", "Second premise removed."));
        var second = kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "2",
            "Second premise removed.",
            dispositions: [new CriterionDispositionRequest("3", "Second waiver invalidates the threshold.")]);

        Xunit.Assert.Contains("Observe the deployment after landing.", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(2, goal.EffectiveAcceptanceCriteriaCorrections.Count);
        Xunit.Assert.Equal("Second waiver invalidates the threshold.",
            Xunit.Assert.Single(second.Dispositions!).Disposition);
    }

    [Xunit.Fact]
    public void LegacyCorrectionSnapshotWithoutDispositionsRestoresAsNoDisposition()
    {
        const string legacyJson = """
            {"SupersededCriterion":"legacy","Correction":"WAIVED: old reason","Actor":"operator","RecordedAt":"2026-09-21T00:00:00Z","SourceTaskId":null,"SourceKind":13,"IsWaiver":true}
            """;
        var legacy = JsonSerializer.Deserialize<EffectiveAcceptanceCriteriaCorrectionSnapshot>(legacyJson)!;
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateActiveGoal(kernel, ["legacy"]);
        var snapshot = kernel.ExportSnapshot();
        var restoredSnapshot = snapshot with
        {
            Goals = [snapshot.Goals[0] with { EffectiveAcceptanceCriteriaCorrections = [legacy] }]
        };

        var restored = AgentOrchestratorKernel.FromSnapshot(restoredSnapshot).GetGoal(goal.Id);

        Xunit.Assert.Null(Xunit.Assert.Single(restored.EffectiveAcceptanceCriteriaCorrections).Dispositions);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoalWithOutstandingCriteria(
        params int[] criterionIndexes)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateGoal(kernel,
        [
            "Implement the mechanism.",
            "Measure the mechanism after landing.",
            "Observe the deployment after landing."
        ]);
        foreach (var criterionIndex in criterionIndexes)
        {
            MapOutstanding(kernel, goal, criterionIndex);
        }
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal);
    }

    private static Goal CreateActiveGoal(AgentOrchestratorKernel kernel, IReadOnlyList<string> criteria)
    {
        var goal = CreateGoal(kernel, criteria);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return goal;
    }

    private static Goal CreateGoal(AgentOrchestratorKernel kernel, IReadOnlyList<string> criteria)
    {
        var goal = kernel.CreateGoal("Protect deferred criterion premises");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Require explicit waiver-time dispositions.",
            criteria,
            VerificationClass.TestVerifiable,
            [],
            []));
        return goal;
    }

    private static CriterionEvidenceObligation MapOutstanding(
        AgentOrchestratorKernel kernel,
        Goal goal,
        int criterionIndex) =>
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            criterionIndex,
            criterionVersion: 1,
            CriterionEvidenceOwner.Acceptance,
            "operator",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: "candidate-a");
}
