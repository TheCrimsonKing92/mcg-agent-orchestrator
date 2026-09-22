using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerEvidenceDispatchTests
{
    [Xunit.Fact]
    public void RefreshLatestProcessRetainsProspectiveEvidenceWithoutHoldingPlanner()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Prospective acceptance evidence", [planner, developer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Build a candidate before the operator observes it.",
            ["Operator observes the candidate after implementation."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-prospective-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        var evidenceRequest =
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"live-candidate-observation\",\"availability\":\"post-implementation\",\"owner\":\"operator\",\"needed\":\"live and stopped observation\",\"reason\":\"the candidate must exist first\"}";
        var plannerPlan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
            "1. disposition=planned; plan=retain the operator-owned observation until the candidate exists",
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars),
            plannerPlan,
            evidenceRequest,
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000),
            "WORKER_RESULT:",
            "files: none",
            "commands: source inspection",
            "tests: not-run - Planner role",
            "commit: none",
            "blockers: none",
            "model_fit: Anthropic/claude-opus-5 - adequate - planning - mapped the evidence lifecycle",
            "skills: criterion-ownership-planning",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4244, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, request.Kind);
        Assert.Equal("operator", request.EvidenceOwner);
        Assert.Empty(kernel.GetPendingBlockingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, planner.LastVerification!.HumanInputKind);
        Assert.Equal("operator", planner.LastVerification.HumanInputEvidenceOwner);
        Assert.DoesNotContain("PLANNER_EVIDENCE_REQUEST:", planner.LastVerification.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.False(kernel.BuildGoalAcceptanceSummary(goal.Id).IsAccepted);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessRetainsLatePlannerEvidenceRequestAndCreatesStructuredHumanInput()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Planner evidence escalation", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Plan the evidence-backed implementation.",
            ["Retrieve the historical receipt."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        var evidenceRequest =
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"third-round-receipt\",\"availability\":\"retrievable\",\"store\":\".orchestrator/goal-events/<goal>.jsonl\",\"needed\":\"timestamp, stdout, and classifier receipt\",\"reason\":\"the worker cannot read orchestrator state\"}";
        var plannerPlan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
            "1. disposition=planned; plan=retrieve the historical receipt from the named store before implementation",
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars),
            plannerPlan,
            evidenceRequest,
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000),
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning requires operator evidence",
            "commit: none",
            "blockers: operator evidence required",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var expectedFingerprint = HumanInputRequest.BuildPlannerEvidenceFingerprint(1, "third-round-receipt");
        var request = kernel.GetPendingHumanInput(goal.Id).Single();
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Contains("Availability: retrievable", request.Question, StringComparison.Ordinal);
        Assert.Contains(".orchestrator/goal-events/<goal>.jsonl", request.Question, StringComparison.Ordinal);
        Assert.Equal(expectedFingerprint, request.QuestionFingerprint);
        Assert.Equal(expectedFingerprint, request.BlockerFingerprint);
        Assert.Equal(expectedFingerprint, planner.LastVerification!.HumanInputQuestionFingerprint);
        Assert.Equal(expectedFingerprint, planner.LastVerification.HumanInputBlockerFingerprint);
        Assert.DoesNotContain("PLANNER_EVIDENCE_REQUEST:", planner.LastVerification.StandardOutput, StringComparison.Ordinal);
        // The directive text is stripped from the recorded stdout above, so the kernel reparse cannot
        // recover the classification. It must ride on the verification record or the typed kind is
        // lost on exactly this path.
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, planner.LastVerification.HumanInputKind);
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, request.Kind);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessLeavesPlainHumanInputTaskPrivate()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Planner clarification", [planner]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-clarification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            "HUMAN_INPUT: Should the planner assume the cheaper lane?",
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning requires an operator decision",
            "commit: none",
            "blockers: operator decision required",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4243, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var request = kernel.GetPendingHumanInput(goal.Id).Single();
        Assert.Equal(HumanWaitKind.SpecClarification, request.Kind);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessRejectsMalformedLatePlannerEvidenceRequestExplicitly()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Malformed Planner evidence escalation", [planner]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-evidence-malformed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars),
            "PLANNER_EVIDENCE_REQUEST: not-json",
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000)));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        Assert.Equal(WorkTaskStatus.Failed, planner.Status);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains(
            "Malformed PLANNER_EVIDENCE_REQUEST: payload is not valid JSON",
            planner.LastVerification!.StandardError,
            StringComparison.Ordinal);
    }
}
