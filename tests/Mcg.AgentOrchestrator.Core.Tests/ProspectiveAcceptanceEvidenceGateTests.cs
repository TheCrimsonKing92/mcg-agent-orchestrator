using Mcg.AgentOrchestrator.Core;

public sealed class ProspectiveAcceptanceEvidenceGateTests
{
    [Xunit.Fact]
    public void ParsedProspectiveEvidenceAllowsImplementationButBlocksAcceptanceUntilOperatorReceipt()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Observe the candidate after implementation.", [planner, developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var stdout = PlannerResult(
            "post-implementation",
            "\"owner\":\"operator\",",
            "live and stopped two-process observation",
            "the candidate must exist before the observation can run");

        kernel.RecordTaskDispatch(
            goal.Id,
            planner.Id,
            new TaskDispatchRecord("planner", "plan", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "plan",
                "C:\\repo",
                0,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true,
                HeartbeatStandardOutputBytes: stdout.Length));

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Empty(kernel.GetPendingBlockingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, request.Kind);
        Assert.Contains("Owner: operator", request.Question, StringComparison.Ordinal);
        Assert.False(request.IsAutoDefaultable);
        Assert.False(request.IsDismissible);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Contains(
            kernel.BuildNextActions(goal.Id).Items,
            item => item.Kind == NextActionKind.RunAssignedTask && item.TaskId == developer.Id);
        Assert.Equal(
            StageReadinessStatus.Verified,
            kernel.BuildStageReadinessReport(goal.Id).Stages.Single(stage => stage.TaskId == planner.Id).StageStatus);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var restoredRequest = Assert.Single(restored.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, restoredRequest.Kind);
        Assert.Equal(
            HumanInputRequest.BuildPlannerEvidenceFingerprint(1, "candidate-observation"),
            restoredRequest.QuestionFingerprint);
        Assert.Contains("Owner: operator", restoredRequest.Question, StringComparison.Ordinal);
        Assert.Empty(restored.GetPendingBlockingHumanInput(goal.Id));
        Assert.Equal(GoalStatus.Active, restored.GetGoal(goal.Id).Status);

        var duplicate = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            planner.Id,
            request.Question,
            request.Kind,
            questionFingerprint: request.QuestionFingerprint,
            blockerFingerprint: request.BlockerFingerprint);
        Assert.True(duplicate.WasReused);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);

        kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Implemented.");
        kernel.RecordTaskVerification(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord("focused test", "C:\\repo", 0, "passed", string.Empty, clock.UtcNow));

        var blocked = kernel.BuildGoalAcceptanceSummary(goal.Id);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.False(blocked.IsAccepted);
        Assert.Equal(1, blocked.PendingHumanInputCount);
        var blocker = Assert.Single(blocked.Blockers);
        Assert.Equal(request.Id, blocker.HumanInputRequestId);

        const string receipt = "Operator observed live/stopped behavior on candidate abc123.";
        kernel.SubmitHumanInput(request.Id, receipt);

        var accepted = kernel.BuildGoalAcceptanceSummary(goal.Id);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(0, accepted.PendingHumanInputCount);
        Assert.Equal(receipt, request.Answer);
    }

    [Xunit.Fact]
    public void ParsedRetrievableEvidenceRemainsAPlanningPrerequisite()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Require a missing design input.", [planner, developer]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var stdout = PlannerResult(
            "retrievable",
            "\"store\":\"design-decisions.db\",",
            "the selected persistence contract",
            "the worker cannot access the decision store");

        kernel.RecordTaskDispatch(
            goal.Id,
            planner.Id,
            new TaskDispatchRecord("planner", "plan", "C:\\repo", clock.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "plan",
                "C:\\repo",
                0,
                stdout,
                string.Empty,
                clock.UtcNow,
                WorkerResultPresent: true,
                HeartbeatStandardOutputBytes: stdout.Length));

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, request.Kind);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Single(kernel.GetPendingBlockingHumanInput(goal.Id));
    }

    private static string PlannerResult(
        string availability,
        string availabilityField,
        string needed,
        string reason) =>
        string.Join(
            Environment.NewLine,
            $"PLANNER_EVIDENCE_REQUEST: {{\"criterion_index\":1,\"evidence_key\":\"candidate-observation\",\"availability\":\"{availability}\",{availabilityField}\"needed\":\"{needed}\",\"reason\":\"{reason}\"}}",
            "WORKER_RESULT:",
            "files: none",
            "commands: source inspection",
            "tests: not-run - Planner role",
            "commit: none",
            "blockers: none",
            "model_fit: Anthropic/claude-opus-5 - adequate - planning - mapped the lifecycle",
            "skills: criterion-ownership-planning",
            "confidence: high",
            "END_WORKER_RESULT");
}
