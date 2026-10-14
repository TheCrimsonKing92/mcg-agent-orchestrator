using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceWorkerRoutingTests
{
    private const string Candidate = "cf46ada32456e6d3d7d13fbcb27aa6bec90b75fd";
    private const string StableId = "acceptance-lifecycle-live-bounce-missing";

    public static IEnumerable<object[]> AuthorityCases()
    {
        foreach (var role in new[] { AgentRole.Tester, AgentRole.Reviewer })
        foreach (var owner in new[] { CriterionEvidenceOwner.Operator, CriterionEvidenceOwner.Acceptance })
        foreach (var scenario in new[] { "current", "unmapped", "legacy", "unknown", "malformed", "duplicate",
            "conflicting", "foreign-id", "missing-candidate", "foreign-candidate", "stale-version",
            "owner-mismatch", "category-mismatch", "correctness", "test-coverage", "not-met", "invalid-contract", "nonzero-exit", "interrupted" })
        {
            if (scenario == "not-met" && role == AgentRole.Tester) continue;
            yield return [role, owner, scenario];
        }
    }

    [Theory]
    [MemberData(nameof(AuthorityCases))]
    public void RecordingRequiresUnambiguousCurrentCandidateAuthority(
        AgentRole role, CriterionEvidenceOwner owner, string scenario)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect source and retain inaccessible evidence", role);
        var goal = kernel.CreateGoal("Feasible worker scope", [task]);
        var spec = new RefinedSpec("Require owner proof", ["Current owner proof", "Source failure path"],
            VerificationClass.TestVerifiable, [], []);
        kernel.RecordGoalRefinement(goal.Id, spec);
        if (scenario != "unmapped")
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                scenario == "owner-mismatch"
                    ? owner == CriterionEvidenceOwner.Operator ? CriterionEvidenceOwner.Acceptance : CriterionEvidenceOwner.Operator
                    : owner,
                "operator", findingStableId: scenario == "foreign-id" ? "foreign-finding" : StableId,
                expectedCandidateSha: Candidate);
        if (scenario == "conflicting")
            kernel.MapCriterionEvidenceOwner(goal.Id, 1, 1,
                owner == CriterionEvidenceOwner.Operator ? CriterionEvidenceOwner.Acceptance : CriterionEvidenceOwner.Operator,
                "operator", findingStableId: StableId, expectedCandidateSha: Candidate);
        if (scenario == "stale-version")
            kernel.RecordGoalRefinement(goal.Id, spec with { AcceptanceCriteria = ["Different required proof", "Source failure path"] });
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var startedAt = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "review", "C:\\fixture",
            startedAt, BaseCommit: scenario switch
            {
                "missing-candidate" => null,
                "foreign-candidate" => "foreign-candidate",
                _ => Candidate
            }));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        if (scenario is "legacy" or "unknown" or "malformed" or "duplicate")
        {
            var mapped = Assert.Single(snapshot.CriterionEvidenceObligations!);
            snapshot = snapshot with { CriterionEvidenceObligations = scenario switch
            {
                "legacy" => [mapped with { ExpectedCandidateSha = null }],
                "unknown" => [mapped with { Owner = CriterionEvidenceOwner.Unknown }],
                "malformed" => [mapped with { Criterion = "Forged criterion text" }],
                _ => [mapped, mapped]
            } };
        }
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        goal = kernel.GetGoal(goal.Id);
        task = Assert.Single(goal.Tasks);
        var category = scenario == "category-mismatch" ? "test-evidence"
            : owner == CriterionEvidenceOwner.Operator ? "operator-owned" : "acceptance-owned";
        var findings = """{"stable_id":"STABLE","state":"open","location":{"file":"docs/operator-runbook.md","region":"owner-proof"},"description":"Current required owner proof is missing.","severity":"blocking","category":"CATEGORY"}"""
            .Replace("STABLE", StableId).Replace("CATEGORY", category);
        if (scenario is "correctness" or "test-coverage")
            findings += """,{"stable_id":"worker-blocker","state":"open","location":{"file":"src/Example.cs","region":"Execute"},"description":"The source failure path is unverified.","severity":"blocking","category":"CATEGORY"}"""
                .Replace("CATEGORY", scenario == "correctness" ? "correctness" : "test-coverage");
        var verdict = scenario == "not-met" ? "not-met" : "not-verifiable";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - OwnerProofTests",
            "commit: none", "blockers: Required owner evidence remains pending.", $"findings: [{findings}]",
            "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"" + verdict + "\",\"evidence\":\"Required owner proof\"},{\"criterion_index\":1,\"verdict\":\"met\",\"evidence\":\"Source inspected\"}]",
            "verdict: needs-work", "model_fit: fixture/model - adequate - deterministic control",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        if (scenario == "invalid-contract") output = output.Replace("confidence: high", "");

        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", scenario == "nonzero-exit" ? 1 : 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: Candidate, DispatchStartedAt: startedAt,
            ProviderFailureKind: scenario == "interrupted" ? ProviderFailureKind.RateLimit : ProviderFailureKind.Unknown)); // worker text cannot replace dispatch identity

        var passes = scenario == "current";
        if (passes) Assert.Equal(WorkTaskStatus.Completed, task.Status);
        else if (scenario == "invalid-contract") Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        else Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(passes, kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.DoesNotContain(goal.CriterionEvidenceObligations, item => item.State == CriterionEvidenceState.Satisfied);
        if (passes)
        {
            var residual = Assert.Single(ReviewFindings.GetOpenBlockingFindings(task.LastVerification!.MergedReviewFindings!, goal.EffectiveAcceptanceCriteriaCorrections));
            Assert.Equal(StableId, residual.StableId);
            var obligation = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
            Assert.Equal(owner, obligation.Owner);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Null(obligation.ReceiptId);
            Assert.Contains(goal.Timeline, item => item.Message.Contains($"owner={owner}; state=Pending", StringComparison.Ordinal) &&
                item.Message.Contains($"next_action={obligation.RequiredScope}", StringComparison.Ordinal) &&
                item.Message.Contains($"candidate_sha={Candidate}", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(goal.Timeline, item => item.Kind ==
                (scenario == "invalid-contract" ? ProgressKind.TaskRetried : ProgressKind.TaskFailed));
            Assert.DoesNotContain(goal.Timeline, item => item.Message.Contains("rule=deferred-off-worker-evidence", StringComparison.Ordinal));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, "current")]
    [Xunit.InlineData(AgentRole.Reviewer, "current")]
    [Xunit.InlineData(AgentRole.Tester, "mixed")]
    [Xunit.InlineData(AgentRole.Reviewer, "mixed")]
    [Xunit.InlineData(AgentRole.Tester, "unmapped")]
    [Xunit.InlineData(AgentRole.Reviewer, "unmapped")]
    [Xunit.InlineData(AgentRole.Tester, "superseded")]
    [Xunit.InlineData(AgentRole.Reviewer, "superseded")]
    [Xunit.InlineData(AgentRole.Reviewer, "not-met")]
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
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow, BaseCommit: "candidate-a"));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        task = Assert.Single(goal.Tasks);
        var findings = """{"stable_id":"missing-full-gate","state":"open","location":{"file":"config/acceptance-manifest.json","region":"full-gate"},"description":"The acceptance executor has not produced a receipt.","severity":"blocking","category":"acceptance-owned"}""";
        if (scenario == "mixed")
            findings += """,{"stable_id":"missing-failure-path","state":"open","location":{"file":"src/Example.cs","region":"Execute"},"description":"The failure path returns success.","severity":"blocking","category":"correctness"}""";
        var criterionVerdict = scenario == "not-met" ? "not-met" : "not-verifiable";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: pass - fixture source checks",
            "commit: none", "blockers: Evidence and findings are recorded below.", $"findings: [{findings}]",
            "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"" + criterionVerdict + "\",\"evidence\":\"See acceptance finding\"},{\"criterion_index\":1,\"verdict\":\"met\",\"evidence\":\"Source inspected; see findings\"}]",
            "verdict: needs-work", "model_fit: fixture/model - deterministic integration control", "skills: none",
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
        if (scenario == "not-met")
        {
            Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed && item.Message.Contains("verdict=not-met", StringComparison.Ordinal));
            Assert.DoesNotContain(goal.Timeline, item => item.Message.Contains("reviewer_verdict=not-met", StringComparison.Ordinal));
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
