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
        Assert.Equal("operator", request.EvidenceOwner);
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
        Assert.Equal("operator", restoredRequest.EvidenceOwner);
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

    [Xunit.Fact]
    public void PassingVerificationCompletesTaskWithOpenProspectiveEvidence()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement the candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Verify without satisfying future evidence.", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Observe the implemented candidate.",
            HumanWaitKind.ProspectiveAcceptanceEvidence).Request;

        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("focused test", "C:\\repo", 0, "passed", string.Empty, clock.UtcNow));

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.False(request.IsCompleted);
        Assert.Equal(GoalStatus.Verified, goal.Status);
    }

    [Xunit.Fact]
    public void AnsweringBlockingRequestRestoresTaskWhenProspectiveEvidenceRemainsOpen()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Plan the candidate.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Keep future evidence while resolving design input.", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var prospective = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Observe the implemented candidate.",
            HumanWaitKind.ProspectiveAcceptanceEvidence,
            questionFingerprint: "criterion-1-evidence").Request;
        var prerequisite = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Which persistence contract should the plan use?",
            HumanWaitKind.PlannerPrerequisiteEvidence,
            questionFingerprint: "criterion-1-evidence").Request;

        kernel.SubmitHumanInput(prerequisite.Id, "Use SQLite.");

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.False(prospective.IsCompleted);
        Assert.Equal(GoalStatus.Active, goal.Status);
    }

    [Xunit.Fact]
    public void VerificationReconciliationIgnoresOpenProspectiveEvidence()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement the candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reconcile verified work before future observation.", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Observe the implemented candidate.",
            HumanWaitKind.ProspectiveAcceptanceEvidence);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Implemented.");
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("focused test", "C:\\repo", 0, "passed", string.Empty, clock.UtcNow));
        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(
            snapshot with
            {
                Goals = [Assert.Single(snapshot.Goals) with { Status = GoalStatus.Completed }]
            },
            clock);

        var reconciled = restored.ReconcileGoalVerificationStatus(goal.Id, "All task gates passed.");

        Assert.True(reconciled);
        Assert.Equal(GoalStatus.Verified, restored.GetGoal(goal.Id).Status);
        Assert.Single(restored.GetPendingHumanInput(goal.Id));
    }

    [Xunit.Fact]
    public void ParkingGoalDoesNotSatisfyProspectiveEvidence()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Preserve future evidence while parked.", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Observe the implemented candidate.",
            HumanWaitKind.ProspectiveAcceptanceEvidence).Request;

        kernel.ParkGoal(goal.Id, "Pause implementation.");

        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(GoalStatus.Parked, goal.Status);
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
