using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each fact owns an in-memory kernel and an injected clock.
public sealed class ReviewerAttestationCriterionNumberTests
{
    [Fact]
    public void InvalidRegistration_NamesMissingAndDuplicatedBriefCriteria()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Explain invalid criterion registration", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review five criteria",
            ["first criterion", "second criterion", "third criterion", "fourth criterion", "fifth criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-pass", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            """[{"criterion_index":0,"verdict":"met","evidence":"First is implemented."},{"criterion_index":1,"verdict":"met","evidence":"Second is implemented."},{"criterion_index":1,"verdict":"met","evidence":"Second is repeated."},{"criterion_index":3,"verdict":"met","evidence":"Fourth is implemented."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-pass", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var failure = Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Contains("Reviewer WORKER_RESULT criteria attestation invalid:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("expected every registered criterion_index 0..4 (criteria 1 to 5) exactly once", failure.Message, StringComparison.Ordinal);
        Assert.Contains("received registered indices 0, 1, 1, 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("missing criteria: 3, 5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("duplicated criteria: 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPassingCriterion_LabelsBriefNumberAndPreservesRepairCommand()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Explain a non-passing criterion", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review two criteria",
            ["first criterion", "second criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        Assert.Empty(goal.CriterionEvidenceObligations);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-pass", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            """[{"criterion_index":0,"verdict":"met","evidence":"First is implemented."},{"criterion_index":1,"verdict":"not-verifiable","evidence":"Required receipt is unavailable."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-pass", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var failure = Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Contains("criteria attestation rejected", failure.Message, StringComparison.Ordinal);
        Assert.Contains("criterion_index=1 (criterion 2) verdict=not-verifiable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--criterion 2 --version", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, failure.Message.Split("criterion_index=", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ExtraAttestation_RecordsBothIndexAndBriefNumberInNote()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var reviewer = new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Explain an extra criterion attestation", [reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Review two criteria",
            ["first criterion", "second criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review-pass", "C:\\repo", clock.UtcNow));
        var result = StructuredReviewerResult(
            """[{"criterion_index":0,"verdict":"met","evidence":"First is implemented."},{"criterion_index":1,"verdict":"met","evidence":"Second is implemented."},{"criterion_index":2,"verdict":"met","evidence":"Extra observation is satisfied."}]""");

        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-pass", "C:\\repo", 0, result, string.Empty, clock.UtcNow, WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Reviewer informational extra criterion attestation recorded:", StringComparison.Ordinal) &&
            evt.Message.Contains("criterion_index=2 (criterion 3); verdict=met", StringComparison.Ordinal));
    }

    private static string StructuredReviewerResult(string criteriaVerdictsJson) =>
        string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - deterministic fixture",
            "commit: none",
            "blockers: none",
            "findings: []",
            "touched_anchors: []",
            $"criteria_verdicts: {criteriaVerdictsJson}",
            "verdict: pass",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
}
