using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceWorkerRoutingTests
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, "current")]
    [Xunit.InlineData(AgentRole.Reviewer, "current")]
    [Xunit.InlineData(AgentRole.Tester, "mixed")]
    [Xunit.InlineData(AgentRole.Reviewer, "mixed")]
    [Xunit.InlineData(AgentRole.Tester, "unmapped")]
    [Xunit.InlineData(AgentRole.Reviewer, "unmapped")]
    [Xunit.InlineData(AgentRole.Tester, "superseded")]
    [Xunit.InlineData(AgentRole.Reviewer, "superseded")]
    public void WorkerResultDeferralRequiresCurrentAuthorityAndPreservesCodeBlockers(AgentRole role, string scenario)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect the current source and report missing evidence", role);
        var goal = kernel.CreateGoal("Separate code findings from acceptance evidence", [task]);
        var spec = new RefinedSpec("Verify the current source and execute acceptance",
            ["Current candidate has a full acceptance receipt", "Source handles the failure path"],
            VerificationClass.TestVerifiable, [], []);
        kernel.RecordGoalRefinement(goal.Id, spec);
        if (scenario != "unmapped")
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "operator",
                findingStableId: "missing-full-gate", expectedCandidateSha: "candidate-a");
        if (scenario == "superseded") kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        task = Assert.Single(goal.Tasks);
        var findings = """{"stable_id":"missing-full-gate","state":"open","location":{"file":"config/acceptance-manifest.json","region":"full-gate"},"description":"The acceptance executor has not produced a receipt.","severity":"blocking","category":"acceptance-owned"}""";
        if (scenario == "mixed")
            findings += """,{"stable_id":"missing-failure-path","state":"open","location":{"file":"src/Example.cs","region":"Execute"},"description":"The failure path returns success.","severity":"blocking","category":"correctness"}""";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: pass - fixture source checks",
            "commit: none", "blockers: Evidence and findings are recorded below.", $"findings: [{findings}]",
            "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"met\",\"evidence\":\"See acceptance finding\"},{\"criterion_index\":1,\"verdict\":\"met\",\"evidence\":\"Source inspected; see findings\"}]",
            "verdict: pass", "model_fit: fixture/model - deterministic integration control", "skills: none",
            "confidence: high", "END_WORKER_RESULT");

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow, WorkerResultPresent: true));

        Assert.Equal(scenario is "current" or "superseded" ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, task.Status);
        var merged = task.LastVerification!.MergedReviewFindings!;
        Assert.Contains(merged, finding => finding.StableId == "missing-full-gate");
        if (scenario == "mixed")
        {
            Assert.Contains(merged, finding => finding.StableId == "missing-failure-path");
            Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed &&
                (role == AgentRole.Tester || item.Message.Contains("missing-failure-path", StringComparison.Ordinal)));
        }
        if (scenario == "current")
        {
            Assert.Single(goal.OutstandingCriterionEvidenceObligations);
            Assert.DoesNotContain(goal.CriterionEvidenceObligations, item => item.State == CriterionEvidenceState.Satisfied);
        }
        if (scenario == "superseded")
            Assert.Contains(goal.Timeline, item => item.Message.Contains("Deferred acceptance-owned finding remains pending", StringComparison.Ordinal));
    }
}
