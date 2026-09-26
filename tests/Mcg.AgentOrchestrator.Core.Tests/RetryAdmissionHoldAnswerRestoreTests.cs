using Mcg.AgentOrchestrator.Core;

public sealed class RetryAdmissionHoldAnswerRestoreTests
{
    [Xunit.Fact]
    public void Answering_retry_admission_recovery_choice_restores_unstarted_dispatch_to_assigned()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Retry held task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retry admission recovery", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var dispatch = new TaskDispatchRecord("codex", "agent run", "C:\\repo", clock.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var fingerprint = new RetryContextFingerprint(RetryContextFingerprint.CurrentSchemaVersion, "unchanged-context");
        var receipt = new RetryAdmissionReceipt(
            "prevented-start", RetryCause.UnchangedContextRepeat, fingerprint,
            RetryAdmissionDecision.Prevented, RetryAdmissionRoute.EnvironmentalHold,
            PaidRouteClassification.Paid, dispatch.DispatchedAt, clock.UtcNow,
            PriorAttemptAt: dispatch.DispatchedAt.AddMinutes(-1));
        kernel.ApplyPreparedRetryAdmission(goal.Id, task.Id,
            new RetryAdmissionResult(RetryAdmissionDecision.Prevented, receipt));
        var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(HumanWaitKind.RecoveryChoice, request.Kind);

        kernel.SubmitHumanInput(request.Id, "The environment recovered.");

        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Xunit.Assert.Null(task.LastProcess);
        Xunit.Assert.Equal(dispatch.DispatchedAt, task.LastDispatch?.DispatchedAt);
        Xunit.Assert.Null(task.RetryAdmissionHoldRoute);
        Xunit.Assert.DoesNotContain(goal.Timeline, item =>
            item.Message == "Human input resolved; restored task status to Running.");
        Xunit.Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.TaskUpdated && item.Message.Contains(dispatch.DispatchedAt.ToString("O")));
        Xunit.Assert.Contains(task.RetryAdmissionHistory, item =>
            item.Decision == RetryAdmissionDecision.RecoveryEvidence && item.Fingerprint == fingerprint);
        kernel.RetryTask(goal.Id, task.Id, "Retry after answered recovery.",
            retryCause: RetryCause.EnvironmentApparatusFailure);
        clock.Advance();
        var creditedDispatch = new TaskDispatchRecord("codex", "agent run", "C:\\repo", clock.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, creditedDispatch);
        var credited = kernel.RecordPreparedRetryAdmission(goal.Id, task.Id, fingerprint,
            PaidRouteClassification.Paid, clock.UtcNow);
        Xunit.Assert.Equal(RetryAdmissionDecision.Allowed, credited.Decision);
        Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4102, "agent run", "C:\\repo", "out", "err", "exit",
                clock.UtcNow, clock.UtcNow.AddSeconds(1), 1));
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("worker", "C:\\repo", 1, "", "Retry failed.",
                clock.UtcNow, DispatchStartedAt: clock.UtcNow));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Retry failed.");
        kernel.RetryTask(goal.Id, task.Id, "Retry after second failure.",
            retryCause: RetryCause.EnvironmentApparatusFailure);
        clock.Advance();
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex", "agent run", "C:\\repo", clock.UtcNow));
        var repeated = kernel.RecordPreparedRetryAdmission(goal.Id, task.Id, fingerprint,
            PaidRouteClassification.Paid, clock.UtcNow);
        Xunit.Assert.Equal(RetryAdmissionDecision.Prevented, repeated.Decision);
        Xunit.Assert.Equal(RetryCause.UnchangedContextRepeat, repeated.Receipt.Cause);
        Xunit.Assert.Equal(HumanWaitKind.RecoveryChoice,
            Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Kind);
    }

    [Xunit.Fact]
    public void Answering_wait_while_live_process_runs_still_restores_running()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Running worker", AgentRole.Developer);
        var goal = kernel.CreateGoal("Live process restore", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex", "agent run", "C:\\repo", clock.UtcNow));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id,
            new TaskProcessRecord(4101, "agent run", "C:\\repo", "out", "err", "exit",
                clock.UtcNow, null, null));
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");

        kernel.SubmitHumanInput(request.Id, "Use option A.");

        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
    }

    [Xunit.Fact]
    public void Answering_wait_with_unrefused_prepared_dispatch_keeps_running()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Prepared worker", AgentRole.Developer);
        var goal = kernel.CreateGoal("Prepared dispatch restore", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex", "agent run", "C:\\repo", clock.UtcNow));
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");

        kernel.SubmitHumanInput(request.Id, "Use option A.");

        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
    }
}
