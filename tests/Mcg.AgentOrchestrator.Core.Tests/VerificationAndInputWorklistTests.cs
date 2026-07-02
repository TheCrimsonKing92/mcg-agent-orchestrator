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

    kernel.RecordAcceptanceFailure(goal.Id, ["merge"]);

    var failedAcceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.False(failedAcceptance.IsAccepted);
    Assert.Equal(GoalStatus.Completed, failedAcceptance.Status);
    var acceptanceBlocker = Assert.Single(failedAcceptance.Blockers);
    Assert.Equal(GoalAcceptanceBlockerKind.AcceptanceFailed, acceptanceBlocker.Kind);
    Assert.Null(acceptanceBlocker.TaskId);
    Assert.True(acceptanceBlocker.Message.Contains("merge", StringComparison.Ordinal));
    Assert.True(acceptanceBlocker.SuggestedAction.Contains($"acceptance for goal {goal.Id.Value[..8]}", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Reviewer_WORKER_RESULT_blockers_fail_gate_and_keep_goal_out_of_acceptance")]
    public void ReviewerWorkerResultBlockersFailGateAndKeepGoalOutOfAcceptance()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Review must block acceptance",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    var stdout = string.Join(Environment.NewLine,
        "Review complete.",
        "WORKER_RESULT:",
        "files: src/Foo.cs",
        "commands: dotnet test --filter Foo",
        "tests: Passed",
        "commit: abc123",
        "blockers: Finding A blocks acceptance",
        "model_fit: OpenAI/gpt-5.5 - adequate - review",
        "skills: none",
        "confidence: high",
        "END_WORKER_RESULT");
    kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Completed, "Reviewer done.");

    kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
        "reviewer stdout",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();
    var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
    var monitor = kernel.BuildMonitor(goal.Id);

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
    Assert.Equal(VerificationGateStatus.FailedVerification, gate.GateStatus);
    Assert.Equal(VerificationGateReason.ReviewerWorkerResultBlocker, gate.Reason);
    Assert.Contains(gate.Message, text => text.Contains("Finding A blocks acceptance", StringComparison.Ordinal));
    Assert.Contains(reviewer.LastVerification!.StandardOutput, text => text.Contains("Finding A blocks acceptance", StringComparison.Ordinal));
    Assert.False(acceptance.IsAccepted);
    Assert.Contains(acceptance.Blockers, blocker =>
        blocker.TaskId == reviewer.Id &&
        blocker.Kind == GoalAcceptanceBlockerKind.VerificationFailed &&
        blocker.Message.Contains("Finding A blocks acceptance", StringComparison.Ordinal));
    Assert.Contains(monitor.AttentionItems, item =>
        item.Kind == TaskAttentionKind.FailedVerification &&
        item.TaskId == reviewer.Id &&
        item.Message.Contains("Finding A blocks acceptance", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Reviewer_markdown_decorated_WORKER_RESULT_blockers_fail_gate")]
    public void ReviewerMarkdownDecoratedWorkerResultBlockersFailGate()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Decorated review result must block acceptance",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    var stdout = string.Join(Environment.NewLine,
        "Review complete.",
        "## WORKER_RESULT:",
        "**files**: none",
        "**commands**: none",
        "**tests**: review found a blocker",
        "**commit**: none",
        "**blockers**: decorated blocker survives markdown",
        "**model_fit**: OpenAI/gpt-5.5 - adequate - review",
        "**skills**: none",
        "**confidence**: high",
        "END_WORKER_RESULT");
    kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Completed, "Reviewer done.");

    kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
        "reviewer stdout",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();
    var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
    Assert.Equal(VerificationGateStatus.FailedVerification, gate.GateStatus);
    Assert.Equal(VerificationGateReason.ReviewerWorkerResultBlocker, gate.Reason);
    Assert.Contains(gate.Message, text => text.Contains("decorated blocker survives markdown", StringComparison.Ordinal));
    Assert.Contains(reviewer.LastVerification!.StandardOutput, text => text.Contains("decorated blocker survives markdown", StringComparison.Ordinal));
    Assert.False(acceptance.IsAccepted);
}

    [Xunit.Fact(DisplayName = "Reviewer_WORKER_RESULT_blocker_detection_uses_most_recent_block")]
    public void ReviewerWorkerResultBlockerDetectionUsesMostRecentBlock()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Latest review result controls acceptance",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: none",
        "tests: old review",
        "commit: none",
        "blockers: stale blocker should be ignored",
        "model_fit: OpenAI/gpt-5.5 - adequate - review",
        "skills: none",
        "confidence: medium",
        "END_WORKER_RESULT",
        "Review retried cleanly.",
        "**WORKER_RESULT**:",
        "**files**: none",
        "**commands**: none",
        "**tests**: clean retry",
        "**commit**: none",
        "**blockers**: none",
        "**model_fit**: OpenAI/gpt-5.5 - adequate - review",
        "**skills**: none",
        "**confidence**: high",
        "END_WORKER_RESULT");
    kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Completed, "Reviewer done.");

    kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
        "reviewer stdout",
        "C:\\repo",
        0,
        stdout,
        string.Empty,
        clock.UtcNow));

    var gate = kernel.BuildVerificationGate(goal.Id).Tasks.Single();
    var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.Equal(VerificationGateStatus.Passed, gate.GateStatus);
    Assert.True(acceptance.IsAccepted);
    Assert.Empty(acceptance.Blockers);
}

    [Xunit.Fact(DisplayName = "Reviewer_WORKER_RESULT_without_blockers_can_pass_acceptance_gate")]
    public void ReviewerWorkerResultWithoutBlockersCanPassAcceptanceGate()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Clean review can pass",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Completed, "Reviewer done.");
    kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
        "reviewer stdout",
        "C:\\repo",
        0,
        "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: pass\ncommit: none\nblockers: none - no acceptance blockers\nEND_WORKER_RESULT",
        string.Empty,
        clock.UtcNow));

    var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.True(acceptance.IsAccepted);
    Assert.Empty(acceptance.Blockers);
}

    [Xunit.Fact(DisplayName = "NonReviewer_WORKER_RESULT_blockers_remain_advisory_for_existing_acceptance_path")]
    public void NonReviewerWorkerResultBlockersRemainAdvisoryForExistingAcceptancePath()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Developer blockers remain advisory",
        [new TaskSpec(TaskId.New(), "Implement result", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Developer)]);
    var developer = goal.Tasks.Single();
    kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Developer done.");
    kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
        "developer stdout",
        "C:\\repo",
        0,
        "WORKER_RESULT:\nfiles: src/Foo.cs\ncommands: dotnet test\ntests: pass\ncommit: abc123\nblockers: API rate limit hit; retry later\nEND_WORKER_RESULT",
        string.Empty,
        clock.UtcNow));

    var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.Equal(GoalStatus.Completed, goal.Status);
    Assert.True(acceptance.IsAccepted);
    Assert.Empty(acceptance.Blockers);
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

    [Xunit.Fact(DisplayName = "HumanWaitPolicy_defaults_and_dismisses_stale_spec_clarifications_only")]
    public void HumanWaitPolicyDefaultsAndDismissesStaleSpecClarificationsOnly()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var defaultGoal = kernel.CreateGoal("Default stale clarification");
    var dismissGoal = kernel.CreateGoal("Dismiss stale clarification");
    var approvalGoal = kernel.CreateGoal("Keep approval");
    var riskGoal = kernel.CreateGoal("Keep risk review");
    var defaultWait = kernel.RequestHumanInput(defaultGoal.Id, null, "Use proposed scope?", suggestedDefaultAnswer: "Use proposed scope.");
    var dismissWait = kernel.RequestHumanInput(dismissGoal.Id, null, "Still relevant?", isAnswerRequired: false);
    var approvalWait = kernel.RequestHumanInput(approvalGoal.Id, null, "Approve paid run?", HumanWaitKind.OperatorApproval);
    var riskWait = kernel.RequestHumanInput(riskGoal.Id, null, "Accept risk?", HumanWaitKind.RiskReview);
    clock.Advance(TimeSpan.FromHours(25));

    var resolved = kernel.SweepStaleHumanWaits(TimeSpan.FromHours(24));

    Assert.Equal(2, resolved.Count);
    Assert.Contains(resolved, item => item.RequestId == defaultWait.Id && item.Resolution == HumanWaitPolicyResolution.Defaulted);
    Assert.Contains(resolved, item => item.RequestId == dismissWait.Id && item.Resolution == HumanWaitPolicyResolution.Dismissed);
    Assert.True(kernel.GetHumanInputRequest(defaultWait.Id).IsCompleted);
    Assert.Equal("Use proposed scope.", kernel.GetHumanInputRequest(defaultWait.Id).Answer);
    Assert.True(kernel.GetHumanInputRequest(dismissWait.Id).IsCompleted);
    Assert.True(kernel.GetHumanInputRequest(dismissWait.Id).WasDismissed);
    Assert.False(kernel.GetHumanInputRequest(approvalWait.Id).IsCompleted);
    Assert.False(kernel.GetHumanInputRequest(riskWait.Id).IsCompleted);
}

    [Xunit.Fact(DisplayName = "HumanWaitPolicy_never_touches_operator_approval_or_risk_review")]
    public void HumanWaitPolicyNeverTouchesOperatorApprovalOrRiskReview()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var approvalGoal = kernel.CreateGoal("Keep approval forever");
    var riskGoal = kernel.CreateGoal("Keep risk forever");
    var approvalWait = kernel.RequestHumanInput(approvalGoal.Id, null, "Approve?", HumanWaitKind.OperatorApproval, suggestedDefaultAnswer: "yes");
    var riskWait = kernel.RequestHumanInput(riskGoal.Id, null, "Accept?", HumanWaitKind.RiskReview, suggestedDefaultAnswer: "yes");
    clock.Advance(TimeSpan.FromDays(30));

    var resolved = kernel.SweepStaleHumanWaits(TimeSpan.FromHours(24));

    Assert.Empty(resolved);
    Assert.False(kernel.GetHumanInputRequest(approvalWait.Id).IsCompleted);
    Assert.False(kernel.GetHumanInputRequest(riskWait.Id).IsCompleted);
    Assert.False(approvalWait.IsAutoDefaultable);
    Assert.False(approvalWait.IsDismissible);
    Assert.False(riskWait.IsAutoDefaultable);
    Assert.False(riskWait.IsDismissible);
}

    [Xunit.Fact(DisplayName = "Dismissed_human_wait_resumes_same_goal_and_task_without_reinitializing")]
    public void DismissedHumanWaitResumesSameGoalAndTaskWithoutReinitializing()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var task = new TaskSpec(TaskId.New(), "Continue after wait", AgentRole.Developer);
    var goal = kernel.CreateGoal("Resume without restart", [task]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var originalGoalId = goal.Id;
    var originalTaskId = task.Id;
    var wait = kernel.RequestHumanInput(
        goal.Id,
        task.Id,
        "Can this stale question be dismissed?",
        isAnswerRequired: false);

    kernel.DismissHumanInput(wait.Id);

    Assert.Equal(originalGoalId, goal.Id);
    Assert.Equal(originalTaskId, goal.Tasks.Single().Id);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Running, goal.Tasks.Single().Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
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

