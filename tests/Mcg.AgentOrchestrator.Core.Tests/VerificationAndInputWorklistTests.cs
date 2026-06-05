using Mcg.AgentOrchestrator.Core;

public sealed class VerificationAndInputWorklistTests
{
    [Xunit.Fact(DisplayName = "BuildVerificationGate_reports_not_ready_missing_failed_and_passed_tasks")]
    public void BuildVerificationGateReportsNotReadyMissingFailedAndPassedTasks()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Gate verification",
        [
            new TaskSpec(TaskId.New(), "Not ready", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Missing verification", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Failed verification", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Passed verification", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var notReady = goal.Tasks[0];
    var missing = goal.Tasks[1];
    var failed = goal.Tasks[2];
    var passed = goal.Tasks[3];
    kernel.ReportTaskProgress(goal.Id, missing.Id, WorkTaskStatus.Completed, "Done.");
    kernel.ReportTaskProgress(goal.Id, failed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, failed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, passed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, passed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id);

    Assert.False(gate.IsSatisfied);
    Assert.Equal(VerificationGateStatus.NotReady, gate.Tasks.Single(item => item.TaskId == notReady.Id).GateStatus);
    Assert.Equal(VerificationGateStatus.MissingVerification, gate.Tasks.Single(item => item.TaskId == missing.Id).GateStatus);
    Assert.Equal(VerificationGateStatus.FailedVerification, gate.Tasks.Single(item => item.TaskId == failed.Id).GateStatus);
    Assert.Equal(VerificationGateStatus.Passed, gate.Tasks.Single(item => item.TaskId == passed.Id).GateStatus);
    Assert.Contains(gate.Tasks.Single(item => item.TaskId == failed.Id).Message, text => text.Contains("exit 1", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildVerificationWorklist_reports_only_open_verification_work")]
    public void BuildVerificationWorklistReportsOnlyOpenVerificationWork()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Track verification work",
        [
            new TaskSpec(TaskId.New(), "Not ready", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Missing verification", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Failed verification", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Passed verification", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var notReady = goal.Tasks[0];
    var missing = goal.Tasks[1];
    var failed = goal.Tasks[2];
    var passed = goal.Tasks[3];
    kernel.ReportTaskProgress(goal.Id, missing.Id, WorkTaskStatus.Completed, "Done.");
    kernel.ReportTaskProgress(goal.Id, failed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, failed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, passed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, passed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var worklist = kernel.BuildVerificationWorklist(goal.Id);

    Assert.Equal(goal.Id, worklist.GoalId);
    Assert.False(worklist.IsSatisfied);
    Assert.Equal(3, worklist.OpenCount);
    Assert.True(worklist.Items.Select(item => item.TaskId).SequenceEqual([notReady.Id, missing.Id, failed.Id]));
    Assert.False(worklist.Items.Any(item => item.TaskId == passed.Id));
    Assert.Contains(worklist.Items.Single(item => item.TaskId == notReady.Id).SuggestedAction, text => text.Contains("Complete the task", StringComparison.Ordinal));
    Assert.Contains(worklist.Items.Single(item => item.TaskId == missing.Id).SuggestedAction, text => text.Contains("Record verification", StringComparison.Ordinal));
    Assert.Contains(worklist.Items.Single(item => item.TaskId == failed.Id).SuggestedAction, text => text.Contains("Inspect", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "BuildGoalAcceptanceSummary_reports_blockers_and_accepted_state")]
    public void BuildGoalAcceptanceSummaryReportsBlockersAndAcceptedState()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Accept only verified work",
        [
            new TaskSpec(TaskId.New(), "Needs input", AgentRole.Planner),
            new TaskSpec(TaskId.New(), "Missing verification", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Failed verification", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Passed verification", AgentRole.Reviewer)
        ]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var needsInput = goal.Tasks[0];
    var missing = goal.Tasks[1];
    var failed = goal.Tasks[2];
    var passed = goal.Tasks[3];
    var request = kernel.RequestHumanInput(goal.Id, needsInput.Id, "Which branch?");
    kernel.ReportTaskProgress(goal.Id, missing.Id, WorkTaskStatus.Completed, "Done.");
    kernel.ReportTaskProgress(goal.Id, failed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, failed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 1, "", "failed", clock.UtcNow));
    kernel.ReportTaskProgress(goal.Id, passed.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, passed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var blocked = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.False(blocked.IsAccepted);
    Assert.Equal(4, blocked.TotalTasks);
    Assert.Equal(1, blocked.PassedTasks);
    Assert.Equal(3, blocked.OpenVerificationCount);
    Assert.Equal(1, blocked.PendingHumanInputCount);
    Assert.Contains(blocked.Blockers, item => item.Kind == GoalAcceptanceBlockerKind.PendingHumanInput && item.TaskId == needsInput.Id);
    Assert.Contains(blocked.Blockers, item => item.HumanInputRequestId == request.Id);
    Assert.Contains(blocked.Blockers, item => item.Kind == GoalAcceptanceBlockerKind.VerificationMissing && item.TaskId == missing.Id);
    Assert.Contains(blocked.Blockers, item => item.Kind == GoalAcceptanceBlockerKind.VerificationFailed && item.TaskId == failed.Id);
    Assert.Contains(blocked.Blockers, item => item.Kind == GoalAcceptanceBlockerKind.VerificationNotReady && item.TaskId == needsInput.Id);

    kernel.SubmitHumanInput(kernel.GetPendingHumanInput(goal.Id).Single().Id, "Use main.");
    kernel.ReportTaskProgress(goal.Id, needsInput.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, needsInput.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, missing.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));
    kernel.RecordTaskVerification(goal.Id, failed.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    var accepted = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.True(accepted.IsAccepted);
    Assert.Equal(GoalStatus.Completed, accepted.Status);
    Assert.Equal(4, accepted.PassedTasks);
    Assert.Equal(0, accepted.OpenVerificationCount);
    Assert.Equal(0, accepted.PendingHumanInputCount);
    Assert.Empty(accepted.Blockers);
}
    [Xunit.Fact(DisplayName = "BuildHumanInputWorklist_reports_pending_questions_with_context")]
    public void BuildHumanInputWorklistReportsPendingQuestionsWithContext()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Resolve operator questions");
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var plannerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var testerTask = goal.Tasks.First(task => task.RequiredRole == AgentRole.Tester);
    var answered = kernel.RequestHumanInput(goal.Id, plannerTask.Id, "Which branch?");
    clock.Advance();
    var goalScoped = kernel.RequestHumanInput(goal.Id, null, "Which repository?");
    clock.Advance();
    var testerScoped = kernel.RequestHumanInput(goal.Id, testerTask.Id, "Which test command?");
    kernel.SubmitHumanInput(answered.Id, "Use main.");

    var worklist = kernel.BuildHumanInputWorklist(goal.Id);

    Assert.Equal(goal.Id, worklist.GoalId);
    Assert.Equal(2, worklist.OpenCount);
    Assert.True(worklist.Items.Select(item => item.RequestId).SequenceEqual([goalScoped.Id, testerScoped.Id]));
    Assert.False(worklist.Items.Any(item => item.RequestId == answered.Id));

    var goalItem = worklist.Items.Single(item => item.RequestId == goalScoped.Id);
    Assert.Equal<TaskId?>(null, goalItem.TaskId);
    Assert.Equal<AgentRole?>(null, goalItem.Role);
    Assert.Equal<WorkTaskStatus?>(null, goalItem.TaskStatus);
    Assert.Equal("Which repository?", goalItem.Question);

    var taskItem = worklist.Items.Single(item => item.RequestId == testerScoped.Id);
    Assert.Equal(testerTask.Id, taskItem.TaskId);
    Assert.Equal(AgentRole.Tester, taskItem.Role);
    Assert.Equal(testerTask.Description, taskItem.Description);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, taskItem.TaskStatus);
    Assert.Contains(taskItem.SuggestedAction, text => text.Contains("Answer", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "Passing_task_verification_completes_running_task_without_open_human_input")]
    public void PassingTaskVerificationCompletesRunningTaskWithoutOpenHumanInput()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete on proof", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");

    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("dotnet test", "C:\\repo", 0, "ok", "", clock.UtcNow));

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
    Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
}
    [Xunit.Fact(DisplayName = "Answered_human_input_keeps_verified_task_completed")]
    public void AnsweredHumanInputKeepsVerifiedTaskCompleted()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Complete after input", [new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started.");
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");
    kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", "C:\\repo", 0, "ok", "", clock.UtcNow));

    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

    kernel.SubmitHumanInput(request.Id, "Use main.");

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
}
}

