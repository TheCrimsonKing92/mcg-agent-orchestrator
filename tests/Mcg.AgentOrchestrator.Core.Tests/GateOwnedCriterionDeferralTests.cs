using Mcg.AgentOrchestrator.Core;

public sealed class GateOwnedCriterionDeferralTests
{
    private const string Candidate = "candidate-c";

    [Xunit.Fact]
    public void GateOwnedNotVerifiableVerdictBindsPendingObligationAndVerifiesGoal()
    {
        var scenario = CreateReviewScenario(gateOwned: true);

        RecordReview(scenario, "not-verifiable");

        Xunit.Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Xunit.Assert.Equal(GoalStatus.Verified, scenario.Goal.Status);
        var obligation = Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        Xunit.Assert.Contains(scenario.Goal.Timeline, item =>
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("Deferred criterion evidence remains pending", StringComparison.Ordinal) &&
            item.Message.Contains(obligation.Id, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void WorkerOwnedNotVerifiableVerdictStillFailsClosedWithRepairCommand()
    {
        var scenario = CreateReviewScenario(gateOwned: false);

        RecordReview(scenario, "not-verifiable");

        Xunit.Assert.Equal(WorkTaskStatus.Failed, scenario.Reviewer.Status);
        var failure = Xunit.Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.Contains("attestation rejected", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("criterion-evidence-map --goal", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"0 1 acceptance {CriterionEvidenceScopes.FullAcceptanceGate}", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(Candidate, failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GateOwnedMetVerdictIsAdvisoryAndStillBindsPendingObligation()
    {
        var scenario = CreateReviewScenario(gateOwned: true);

        RecordReview(scenario, "met");

        Xunit.Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        var obligation = Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        Xunit.Assert.Contains(scenario.Goal.Timeline, item =>
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("met verdict is advisory", StringComparison.Ordinal) &&
            item.Message.Contains("gate remains the evidence", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void WaivedGateOwnedMetVerdictDoesNotBindObligation()
    {
        var scenario = CreateReviewScenario(gateOwned: true);
        scenario.Kernel.WaiveAcceptanceCriterion(
            scenario.Goal.Id,
            scenario.Goal.RefinedSpec!.AcceptanceCriteria[0],
            "Operator accepts the criterion without gate evidence.",
            "operator@example");

        RecordReview(scenario, "met");

        Xunit.Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        var obligation = Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Null(obligation.ExpectedCandidateSha);
        Xunit.Assert.DoesNotContain(scenario.Goal.Timeline, item =>
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("met verdict is advisory", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BriefAndRoleGuidanceIdentifyAcceptanceGateOwnedCriteria()
    {
        const string gateCriterion = "The full acceptance gate passes.";
        const string operatorCriterion = "A human confirms the deployed host.";
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Route evidence", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Route evidence to its feasible owner.",
            [gateCriterion, operatorCriterion],
            VerificationClass.TestVerifiable,
            [],
            [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [gateCriterion, operatorCriterion],
            OperatorOwnedAcceptanceCriteria = [operatorCriterion]
        });

        var brief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        var gateSection = brief[brief.IndexOf("ACCEPTANCE-GATE-OWNED", StringComparison.Ordinal)..];
        Xunit.Assert.Contains(gateCriterion, gateSection, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain($"- {operatorCriterion}", gateSection, StringComparison.Ordinal);

        foreach (var requirements in new[]
                 {
                     SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer),
                     SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer, TaskComplexity.Simple),
                     SdlcRolePromptRequirements.BuildPlainText(AgentRole.Tester),
                     SdlcRolePromptRequirements.BuildPlainText(AgentRole.Tester, TaskComplexity.Simple)
                 })
        {
            Xunit.Assert.Contains("ACCEPTANCE-GATE-OWNED", requirements, StringComparison.Ordinal);
            Xunit.Assert.Contains("OPERATOR-OWNED", requirements, StringComparison.Ordinal);
            Xunit.Assert.Contains("not-verifiable", requirements, StringComparison.Ordinal);
            Xunit.Assert.Contains("otherwise attest met", requirements, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("does not fail the round", requirements, StringComparison.Ordinal);
        }

        var compactReviewer = SdlcRolePromptRequirements.BuildPlainText(AgentRole.Reviewer, TaskComplexity.Simple);
        Xunit.Assert.True(
            compactReviewer.Length <= SdlcRolePromptRequirements.ReviewerCompactRequirementsMaxChars,
            $"Compact Reviewer requirements are {compactReviewer.Length} chars.");
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) CreateReviewScenario(bool gateOwned)
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Route gate evidence", [reviewer]);
        var spec = new RefinedSpec(
            "Require deterministic gate evidence.",
            ["The full acceptance gate passes."],
            VerificationClass.TestVerifiable,
            [],
            []);
        if (gateOwned)
            spec = spec with { AcceptanceGateOwnedAcceptanceCriteria = [spec.AcceptanceCriteria[0]] };
        kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, Candidate);
        return (kernel, goal, reviewer);
    }

    private static void RecordReview(
        (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) scenario,
        string criterionVerdict)
    {
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic fixture",
            "commit: none",
            "blockers: none",
            "findings: []",
            "touched_anchors: []",
            $"criteria_verdicts: [{{\"criterion_index\":0,\"verdict\":\"{criterionVerdict}\",\"evidence\":\"full acceptance gate\"}}]",
            "verdict: pass",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(
            scenario.Goal.Id,
            scenario.Reviewer.Id,
            new TaskVerificationRecord(
                "review",
                "C:\\fixture",
                0,
                output,
                string.Empty,
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true,
                ReviewedCommit: Candidate));
    }
}
