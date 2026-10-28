using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its in-memory kernel and serialized snapshot.
public sealed class BriefRevisionVerificationClassTests
{
    private const string GateCriterion =
        "The revised count is correct. Acceptance owns execution. TEST-VERIFIABLE.";
    private const string WorkerCriterion =
        "The revised algorithm is inspected. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
    private const string OperatorCriterion =
        "The live count is observed. Operator owns execution. REAL-WORLD-DEPENDENT.";

    [Xunit.Fact]
    public void ChangedAllTestCriteria_ReclassifiesAndPreservesSpecStateAfterReload()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(OperatorCriterion));
        var decision = new RefinedSpecDecision("Which algorithm?", "Existing algorithm", "Stable contract");
        var question = new RefinedSpecOpenQuestion("unrelated", "Which label?", "behavior", "Open");
        var answer = new HumanInputAnswerRecord("answer", "Keep the format", DateTimeOffset.UnixEpoch,
            BriefVersion: 1);
        var original = new RefinedSpec("Observe counts", [OperatorCriterion],
            VerificationClass.RealWorldDependent, [decision], [question])
        {
            OperatorOwnedAcceptanceCriteria = [OperatorCriterion],
            ClarificationAnswerHistory = [answer]
        };
        kernel.SetGoalRefinedSpec(goal.Id, original);
        Xunit.Assert.Equal(VerificationClass.RealWorldDependent, goal.RefinedSpec!.VerificationClass);

        var result = kernel.ReviseGoalBrief(goal.Id, Brief(GateCriterion, WorkerCriterion));

        Xunit.Assert.True(result.RefinedCriteriaReDerived);
        Xunit.Assert.Equal(2, result.RefinedCriteriaCount);
        var spec = goal.RefinedSpec!;
        Xunit.Assert.Equal([GateCriterion, WorkerCriterion], spec.AcceptanceCriteria);
        // Negative control: unchanged revision derivation retains RealWorldDependent here.
        Xunit.Assert.Equal(VerificationClass.TestVerifiable, spec.VerificationClass);
        Xunit.Assert.Equal([GateCriterion], spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.Worker,
            AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(WorkerCriterion).Kind);
        var pending = Xunit.Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Xunit.Assert.Equal(2, pending.CriterionVersion);
        Xunit.Assert.Equal(GateCriterion, pending.Criterion);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, pending.Owner);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, pending.State);
        Xunit.Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, pending.RequiredScope);
        Xunit.Assert.Equal(2, goal.AuthoritativeBrief.Version);
        Xunit.Assert.Equal(2, goal.AuthoritativeRefinedSpecVersion!.Version);
        Xunit.Assert.Equal(2, goal.AuthoritativeRefinedSpecVersion.BriefVersion);

        var restored = Reload(kernel).GetGoal(goal.Id);
        Xunit.Assert.Equal(VerificationClass.TestVerifiable, restored.RefinedSpec!.VerificationClass);
        Xunit.Assert.Equal(VerificationClass.TestVerifiable,
            restored.AuthoritativeRefinedSpecVersion!.Spec.VerificationClass);
        Xunit.Assert.Equal(spec.BehavioralContract, restored.RefinedSpec.BehavioralContract);
        Xunit.Assert.Equal(decision, Xunit.Assert.Single(restored.RefinedSpec.Decisions));
        Xunit.Assert.Equal(question, Xunit.Assert.Single(restored.RefinedSpec.OpenQuestions));
        Xunit.Assert.True(restored.RefinedSpec.HasOpenQuestions);
        Xunit.Assert.Equal(answer, Xunit.Assert.Single(restored.RefinedSpec.ClarificationAnswerHistory));
        var historical = Xunit.Assert.Single(restored.RefinedSpecVersions, version => version.Version == 1);
        Xunit.Assert.Equal(2, historical.SupersededByVersion);
        Xunit.Assert.Equal(VerificationClass.RealWorldDependent, historical.Spec.VerificationClass);
        Xunit.Assert.Equal([OperatorCriterion], historical.Spec.AcceptanceCriteria);
        Xunit.Assert.Equal([OperatorCriterion], historical.Spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(pending, Xunit.Assert.Single(restored.OutstandingCriterionEvidenceObligations));
    }

    [Xunit.Theory]
    [Xunit.InlineData("TEST-VERIFIABLE.", VerificationClass.TestVerifiable, false, false, false)]
    [Xunit.InlineData("Reviewer owns execution. test-verifiable.", VerificationClass.TestVerifiable, false, false, false)]
    [Xunit.InlineData("Acceptance owns execution.", VerificationClass.TestVerifiable, true, false, false)]
    [Xunit.InlineData("Acceptance owns execution. REAL-WORLD-DEPENDENT.", VerificationClass.TestVerifiable, true, false, false)]
    [Xunit.InlineData("Reviewer owns execution. REAL-WORLD-DEPENDENT. TEST-VERIFIABLE.", VerificationClass.TestVerifiable, false, false, false)]
    [Xunit.InlineData("No classification is declared.", VerificationClass.RealWorldDependent, false, false, false)]
    [Xunit.InlineData("REAL-WORLD-DEPENDENT.", VerificationClass.RealWorldDependent, false, false, true)]
    [Xunit.InlineData("REAL-WORLD-DEPENDENT. TEST-VERIFIABLE.", VerificationClass.RealWorldDependent, false, false, true)]
    [Xunit.InlineData("Operator owns execution. TEST-VERIFIABLE.", VerificationClass.RealWorldDependent, false, true, false)]
    [Xunit.InlineData("Operator owns execution; Acceptance owns execution. TEST-VERIFIABLE.", VerificationClass.RealWorldDependent, false, false, true)]
    [Xunit.InlineData("OPERATOR-OWNED; ACCEPTANCE-GATE-OWNED. TEST-VERIFIABLE.", VerificationClass.RealWorldDependent, false, false, true)]
    [Xunit.InlineData("TEST-VERIFIABLE. An unmarked final sentence.", VerificationClass.RealWorldDependent, false, false, false)]
    public void ChangedCriteria_ConservativeSignals_KeepOwnershipIndependent(
        string signals, VerificationClass expected, bool gateOwned, bool operatorOwned, bool needsQuestion)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(WorkerCriterion));
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Counts", [WorkerCriterion],
            VerificationClass.TestVerifiable, [], []));
        var criterion = $"The additional result is observed. {signals}";

        var result = kernel.ReviseGoalBrief(goal.Id, Brief(GateCriterion, criterion));

        Xunit.Assert.True(result.RefinedCriteriaReDerived);
        Xunit.Assert.Equal(2, result.RefinedCriteriaCount);
        var spec = goal.RefinedSpec!;
        Xunit.Assert.Equal(expected, spec.VerificationClass);
        Xunit.Assert.Equal([GateCriterion, criterion], spec.AcceptanceCriteria);
        Xunit.Assert.Equal(gateOwned ? [GateCriterion, criterion] : new[] { GateCriterion },
            spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(operatorOwned ? new[] { criterion } : [], spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(needsQuestion, spec.HasOpenQuestions);
        if (needsQuestion)
        {
            var question = Xunit.Assert.Single(spec.OpenQuestions);
            Xunit.Assert.Equal(criterion, question.Criterion);
            Xunit.Assert.Equal("ownership-lifecycle", question.ForkKind);
            Xunit.Assert.Equal("ownership-revision:1", question.NormalizedQuestionKey);
        }
        else
            Xunit.Assert.Empty(spec.OpenQuestions);

        var obligations = goal.OutstandingCriterionEvidenceObligations;
        Xunit.Assert.Equal(1 + (gateOwned || operatorOwned ? 1 : 0), obligations.Count);
        var gate = Xunit.Assert.Single(obligations, item => item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, gate.Owner);
        if (gateOwned || operatorOwned)
            Xunit.Assert.Equal(operatorOwned ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance,
                Xunit.Assert.Single(obligations, item => item.CriterionIndex == 1).Owner);
        Xunit.Assert.All(obligations, item =>
        {
            Xunit.Assert.Equal(2, item.CriterionVersion);
            Xunit.Assert.Equal(CriterionEvidenceState.Pending, item.State);
        });
    }

    [Xunit.Fact]
    public void AddedOperatorCriterion_ReclassifiesWithoutReusingPriorReceipts()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(GateCriterion));
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Counts", [GateCriterion],
            VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [GateCriterion]
        });
        var old = Xunit.Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        kernel.RecordCriterionEvidence(goal.Id, old.Id, CriterionEvidenceOwner.Acceptance,
            "candidate-a", "old-gate", old.RequiredScope, true, "Prior gate passed");
        Xunit.Assert.Empty(goal.OutstandingCriterionEvidenceObligations);

        kernel.ReviseGoalBrief(goal.Id, Brief(GateCriterion, OperatorCriterion));
        kernel.RecordCriterionEvidence(goal.Id, old.Id, CriterionEvidenceOwner.Acceptance,
            "candidate-a", "late-old-gate", old.RequiredScope, true, "Late historical gate");

        var restored = Reload(kernel).GetGoal(goal.Id);
        Xunit.Assert.Equal(VerificationClass.RealWorldDependent, restored.RefinedSpec!.VerificationClass);
        Xunit.Assert.Equal([GateCriterion], restored.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([OperatorCriterion], restored.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(CriterionEvidenceState.Satisfied,
            Xunit.Assert.Single(restored.CriterionEvidenceObligations, item => item.Id == old.Id).State);
        var current = restored.GetOutstandingCriterionEvidenceObligations("candidate-a");
        Xunit.Assert.Equal(2, current.Count);
        Xunit.Assert.Equal([CriterionEvidenceOwner.Acceptance, CriterionEvidenceOwner.Operator],
            current.OrderBy(item => item.CriterionIndex).Select(item => item.Owner));
        Xunit.Assert.All(current, item =>
        {
            Xunit.Assert.Equal(2, item.CriterionVersion);
            Xunit.Assert.Equal(CriterionEvidenceState.Pending, item.State);
            Xunit.Assert.Null(item.ReceiptId);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(VerificationClass.TestVerifiable, false)]
    [Xunit.InlineData(VerificationClass.TestVerifiable, true)]
    [Xunit.InlineData(VerificationClass.RealWorldDependent, false)]
    [Xunit.InlineData(VerificationClass.RealWorldDependent, true)]
    public void UnchangedOrAbsentCriteria_RetainsAggregateAndSpecVersion(
        VerificationClass initialClass, bool absent)
    {
        const string criterion = "The existing criterion has no classification.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(criterion));
        var original = new RefinedSpec("Existing contract", [criterion], initialClass, [], []);
        kernel.SetGoalRefinedSpec(goal.Id, original);

        var result = kernel.ReviseGoalBrief(goal.Id,
            absent ? "Revised prose only. TEST-VERIFIABLE." : "New surrounding prose.\n" + Brief(criterion));

        Xunit.Assert.False(result.RefinedCriteriaReDerived);
        Xunit.Assert.Null(result.RefinedCriteriaCount);
        Xunit.Assert.Equal(2, goal.AuthoritativeBrief.Version);
        Xunit.Assert.Equal(original, goal.RefinedSpec);
        var restored = Reload(kernel).GetGoal(goal.Id);
        Xunit.Assert.Equal(initialClass, restored.RefinedSpec!.VerificationClass);
        var version = Xunit.Assert.Single(restored.RefinedSpecVersions);
        Xunit.Assert.Equal(1, version.Version);
        Xunit.Assert.Equal(1, version.BriefVersion);
        Xunit.Assert.Equal([criterion], version.Spec.AcceptanceCriteria);
    }

    private static AgentOrchestratorKernel Reload(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);

    private static string Brief(params string[] criteria) =>
        "Brief.\n\n## Acceptance criteria\n\n" +
        string.Join("\n", criteria.Select((criterion, index) => $"{index + 1}. {criterion}"));
}
