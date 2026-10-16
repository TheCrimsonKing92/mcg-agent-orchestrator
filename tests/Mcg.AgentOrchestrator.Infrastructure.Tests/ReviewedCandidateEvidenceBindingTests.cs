using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ReviewedCandidateEvidenceBindingTests
{
    private const string Criterion = "The full acceptance gate passes.";
    private const string FindingId = "gate-proof";

    [Fact]
    public void FindingBearingReviewRebindsBeforeDeferralAndGateRecordsFreshEvidence()
    {
        var (kernel, goal, tasks) = CreateScenario(mapped: true);
        var original = Assert.Single(goal.CriterionEvidenceObligations);

        RecordRound(kernel, goal, tasks[0], "B", finding: true);

        Assert.Equal(WorkTaskStatus.Completed, tasks[0].Status);
        var pending = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, pending.State);
        Assert.Equal("B", pending.ExpectedCandidateSha);
        Assert.Equal(FindingId, pending.FindingStableId);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, pending.Owner);
        Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, pending.RequiredScope);
        Assert.Equal(original.Provenance, pending.Provenance);
        Assert.Contains(goal.Timeline, item => item.Message.Contains("expected_candidate=B", StringComparison.Ordinal));

        Assert.Null(AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, "B", kernel, TrunkBranchName.Default));

        var satisfied = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, satisfied.State);
        Assert.Equal("B", satisfied.CandidateSha);
        Assert.Equal("full-acceptance:B", satisfied.ReceiptId);
    }

    [Theory]
    [InlineData("not-verifiable")]
    [InlineData("met")]
    public void NewReviewReopensReceiptAndSameCandidateReviewPreservesSatisfiedValue(string verdict)
    {
        var (kernel, goal, tasks) = CreateScenario(reviewCount: 3);
        RecordRound(kernel, goal, tasks[0], "A", verdict: verdict);
        Assert.Equal(WorkTaskStatus.Completed, tasks[0].Status);
        Assert.Null(AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, "A", kernel, TrunkBranchName.Default));
        var atA = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, atA.State);
        Assert.Equal("A", atA.CandidateSha);
        Assert.Equal("full-acceptance:A", atA.ReceiptId);

        RecordRound(kernel, goal, tasks[1], "B", verdict: verdict);

        Assert.Equal(WorkTaskStatus.Completed, tasks[1].Status);
        var reopened = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, reopened.State);
        Assert.Equal("B", reopened.ExpectedCandidateSha);
        Assert.Null(reopened.CandidateSha);
        Assert.Null(reopened.ReceiptId);
        Assert.Null(reopened.Detail);
        var archived = Assert.Single(reopened.PriorReceipts!);
        Assert.Equal("full-acceptance:A", archived.ReceiptId);
        Assert.Equal("A", archived.CandidateSha);

        Assert.Null(AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, "B", kernel, TrunkBranchName.Default));
        var atB = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, atB.State);
        Assert.Equal("B", atB.CandidateSha);
        Assert.Equal("full-acceptance:B", atB.ReceiptId);

        RecordRound(kernel, goal, tasks[2], "B", verdict: verdict);

        Assert.Equal(WorkTaskStatus.Completed, tasks[2].Status);
        Assert.Equal(atB, Assert.Single(goal.CriterionEvidenceObligations));
    }

    [Fact]
    public void GateOnUnreviewedCandidateRefusesWithoutExecutionDirectory()
    {
        var (kernel, goal, tasks) = CreateScenario(mapped: true);
        RecordRound(kernel, goal, tasks[0], "B", finding: true);
        Assert.Equal(WorkTaskStatus.Completed, tasks[0].Status);
        var beforeGate = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal("B", beforeGate.ExpectedCandidateSha);

        var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, "C", kernel, TrunkBranchName.Default);

        Assert.Contains("is bound to B", diagnostic);
        Assert.Equal(beforeGate, Assert.Single(goal.CriterionEvidenceObligations));
        Assert.Equal(CriterionEvidenceState.Pending, beforeGate.State);
    }

    [Fact]
    public void ReviewWithoutDispatchBaseCannotReplaceBindingWithWorkerCandidate()
    {
        var (kernel, goal, tasks) = CreateScenario(mapped: true);
        var original = Assert.Single(goal.CriterionEvidenceObligations);

        RecordRound(kernel, goal, tasks[0], null, finding: true);

        Assert.Null(tasks[0].LastVerification!.ReviewedCommit);
        Assert.Equal(original, Assert.Single(goal.CriterionEvidenceObligations));
        Assert.Equal("A", Assert.Single(goal.CriterionEvidenceObligations).ExpectedCandidateSha);
    }

    [Fact]
    public void TesterFindingDoesNotRebindAndStillFails()
    {
        var (kernel, goal, tasks) = CreateScenario(mapped: true, role: AgentRole.Tester);
        var original = Assert.Single(goal.CriterionEvidenceObligations);

        RecordRound(kernel, goal, tasks[0], "B", finding: true);

        Assert.Equal(WorkTaskStatus.Failed, tasks[0].Status);
        Assert.Equal("B", tasks[0].LastVerification!.ReviewedCommit);
        Assert.Equal(original, Assert.Single(goal.CriterionEvidenceObligations));
        Assert.Equal("A", Assert.Single(goal.CriterionEvidenceObligations).ExpectedCandidateSha);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec[] Tasks) CreateScenario(
        bool mapped = false, int reviewCount = 1, AgentRole role = AgentRole.Reviewer)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = Enumerable.Range(0, reviewCount)
            .Select(index => new TaskSpec(TaskId.New(), $"Inspect candidate {index}", role)).ToArray();
        var goal = kernel.CreateGoal("Require gate evidence for the reviewed candidate", tasks);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Require deterministic full-gate evidence", [Criterion], VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = mapped ? [] : [Criterion]
        });
        if (mapped)
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "operator", findingStableId: FindingId, expectedCandidateSha: "A");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal, tasks);
    }

    private static void RecordRound(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string? candidate,
        bool finding = false, string verdict = "not-verifiable")
    {
        var startedAt = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: candidate));
        var findings = finding
            ? """[{"stable_id":"gate-proof","state":"open","location":{"file":"docs/operator-runbook.md","region":"gate-proof"},"description":"Full acceptance evidence remains pending.","severity":"blocking","category":"acceptance-owned"}]"""
            : "[]";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", $"blockers: {(finding ? "Required owner evidence remains pending." : "none")}",
            $"findings: {findings}", "touched_anchors: []",
            $"criteria_verdicts: [{{\"criterion_index\":0,\"verdict\":\"{verdict}\",\"evidence\":\"Full acceptance gate\"}}]",
            $"verdict: {(finding ? "needs-work" : "pass")}",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate", DispatchStartedAt: startedAt));
    }
}
