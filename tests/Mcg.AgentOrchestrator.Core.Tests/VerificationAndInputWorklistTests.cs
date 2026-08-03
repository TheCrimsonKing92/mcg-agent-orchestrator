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
    Assert.Contains("exit 1", gate.Tasks.Single(item => item.TaskId == failed.Id).Message, StringComparison.Ordinal);
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
    Assert.Contains("Complete the task", worklist.Items.Single(item => item.TaskId == notReady.Id).SuggestedAction, StringComparison.Ordinal);
    Assert.Contains("Record verification", worklist.Items.Single(item => item.TaskId == missing.Id).SuggestedAction, StringComparison.Ordinal);
    Assert.Contains("Inspect", worklist.Items.Single(item => item.TaskId == failed.Id).SuggestedAction, StringComparison.Ordinal);
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
    Assert.Contains(blocked.Blockers, item =>
        item.HumanInputRequestId == request.Id &&
        item.Message.Contains("Requests: 1 open / 1 total", StringComparison.Ordinal));
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
    Assert.Equal(GoalStatus.Verified, accepted.Status);
    Assert.Equal(4, accepted.PassedTasks);
    Assert.Equal(0, accepted.OpenVerificationCount);
    Assert.Equal(0, accepted.PendingHumanInputCount);
    Assert.Empty(accepted.Blockers);

    kernel.RecordAcceptanceFailure(goal.Id, ["merge"]);

    var failedAcceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);

    Assert.False(failedAcceptance.IsAccepted);
    Assert.Equal(GoalStatus.Verified, failedAcceptance.Status);
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
        "tests: Failed: 1",
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
    Assert.Contains("Finding A blocks acceptance", gate.Message, StringComparison.Ordinal);
    Assert.Contains("Finding A blocks acceptance", reviewer.LastVerification!.StandardOutput, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Reviewer_WORKER_RESULT_blocker_matching_superseded_criterion_is_suppressed")]
    public void ReviewerWorkerResultBlockerMatchingSupersededCriterionIsSuppressed()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Suppress corrected review blocker",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    kernel.RecordTaskNote(
        goal.Id,
        reviewer.Id,
        "CRITERIA CORRECTION: supersedes=\"full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient\"");
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: none",
        "tests: pass - focused check",
        "commit: none",
        "blockers: missing full Infrastructure suite before review",
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
    Assert.Equal(GoalStatus.Verified, goal.Status);
    Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
    Assert.Equal(VerificationGateStatus.Passed, gate.GateStatus);
    Assert.True(acceptance.IsAccepted);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == reviewer.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("Suppressed Reviewer blocker matching operator criteria correction", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Reviewer_WORKER_RESULT_unrelated_blocker_routes_after_superseded_finding_is_removed")]
    public void ReviewerWorkerResultUnrelatedBlockerRoutesAfterSupersededFindingIsRemoved()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal(
        "Keep unrelated review blocker",
        [new TaskSpec(TaskId.New(), "Review result", AgentRole.Reviewer)]);
    kernel.ActivateGoal(goal.Id, [DefaultAgents().First(agent => agent.Role == AgentRole.Reviewer)]);
    var reviewer = goal.Tasks.Single();
    kernel.RetryTask(
        goal.Id,
        reviewer.Id,
        "CRITERIA CORRECTION: supersedes=\"full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient\"");
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: none",
        "tests: pass - focused check",
        "commit: none",
        "blockers: missing full Infrastructure suite before review; public status output omits the correction overlay",
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
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
    Assert.Equal(VerificationGateReason.ReviewerWorkerResultBlocker, gate.Reason);
    Assert.DoesNotContain("missing full Infrastructure suite before review", gate.Message, StringComparison.Ordinal);
    Assert.Contains("public status output omits the correction overlay", gate.Message, StringComparison.Ordinal);
    Assert.False(acceptance.IsAccepted);
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
    Assert.Contains("decorated blocker survives markdown", gate.Message, StringComparison.Ordinal);
    Assert.Contains("decorated blocker survives markdown", reviewer.LastVerification!.StandardOutput, StringComparison.Ordinal);
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

    Assert.Equal(GoalStatus.Verified, goal.Status);
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

    Assert.Equal(GoalStatus.Verified, goal.Status);
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

    Assert.Equal(GoalStatus.Verified, goal.Status);
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
    Assert.Contains("Answer", taskItem.SuggestedAction, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "Parking_goal_resolves_open_human_waits_with_park_reason")]
    public void ParkingGoalResolvesOpenHumanWaitsWithParkReason()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Park stale waits");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    var goalWait = kernel.RequestHumanInput(goal.Id, null, "Which repository?");
    var taskWait = kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");

    kernel.ParkGoal(goal.Id, "deferred");

    Assert.Equal(GoalStatus.Parked, goal.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.True(goalWait.IsCompleted);
    Assert.True(taskWait.IsCompleted);
    Assert.Equal("Goal parked: deferred", goalWait.Answer);
    Assert.Equal("Goal parked: deferred", taskWait.Answer);
    Assert.False(goalWait.WasDismissed);
    Assert.False(taskWait.WasDismissed);
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

    [Xunit.Fact(DisplayName = "Answering_last_human_input_restores_running_task_and_goal_status")]
    public void AnsweringLastHumanInputRestoresRunningTaskAndGoalStatus()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Resume after input", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");

    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

    kernel.SubmitHumanInput(request.Id, "Use option A.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskUpdated &&
        evt.Message == "Human input resolved; restored task status to Running.");
}

    [Xunit.Fact(DisplayName = "Refreshing_parked_goal_with_resolved_human_wait_promotes_after_park_decision")]
    public void RefreshingParkedGoalWithResolvedHumanWaitPromotesAfterParkDecision()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Resume parked answered wait", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");
    goal.SetStatus(GoalStatus.Parked);
    clock.Advance();
    goal.Append(new ProgressEvent(
        goal.Id,
        null,
        ProgressKind.GoalPolicyDecision,
        "Goal parked: waiting for operator answer",
        clock.UtcNow));
    clock.Advance();

    kernel.SubmitHumanInput(request.Id, "Use option A.");

    Assert.Equal(GoalStatus.Parked, goal.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));

    var promoted = kernel.RefreshParkedGoalsWithResolvedHumanWaits();

    Assert.Equal(1, promoted);
    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Running, goal.Tasks.Single().Status);
    Assert.Contains(goal.Timeline, evt =>
        evt.Kind == ProgressKind.GoalPolicyDecision &&
        evt.Message == "Goal unparked: resolved parked human wait.");
}

    [Xunit.Fact(DisplayName = "Answering_human_input_restores_completed_dispatch_without_verification_to_assigned")]
    public void AnsweringHumanInputRestoresCompletedDispatchWithoutVerificationToAssigned()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Redispatch after input", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(123, "agent run", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, null, null));
    kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, new TaskProcessRecord(123, "agent run", "C:\\repo", "out.log", "err.log", "exit.txt", clock.UtcNow, clock.UtcNow, 1), null);
    var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which option?");

    kernel.SubmitHumanInput(request.Id, "Use option B.");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskUpdated &&
        evt.Message == "Human input resolved; restored task status to Assigned.");
}

    [Xunit.Fact(DisplayName = "Answering_one_of_multiple_human_inputs_does_not_restore_prematurely")]
    public void AnsweringOneOfMultipleHumanInputsDoesNotRestorePrematurely()
{
    var clock = new FakeClock();
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Wait for both answers", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, DefaultAgents());
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", clock.UtcNow));
    var first = kernel.RequestHumanInput(goal.Id, task.Id, "First choice?");
    var second = kernel.RequestHumanInput(goal.Id, task.Id, "Second choice?");

    kernel.SubmitHumanInput(first.Id, "A");

    Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
    Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
    Assert.Single(kernel.GetPendingHumanInput(goal.Id));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskUpdated &&
        evt.Message.Contains("restored task status", StringComparison.Ordinal));

    kernel.SubmitHumanInput(second.Id, "B");

    Assert.Equal(GoalStatus.Active, goal.Status);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
}

    [Xunit.Fact]
    public void RequestHumanInput_same_normalized_question_reuses_open_request()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Deduplicate human input",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();

        var first = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Authorize expanding scope?",
            blockerFingerprint: "same-blocker");
        var second = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "  AUTHORIZE   expanding\tSCOPE?  ",
            blockerFingerprint: "changed-blocker");

        Assert.True(first.WasCreated);
        Assert.True(second.WasReused);
        Assert.Equal(first.Request.Id, second.Request.Id);
        Assert.Equal(new HumanInputRequestCounts(1, 1), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.Equal(1, first.Request.SuppressionCount);
        Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputRequested));
        Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.DuplicateHumanInputSuppressed &&
            item.Message.Contains(first.Request.Id.Value[..8], StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task RequestHumanInput_concurrent_matches_persist_one_id()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Serialize human input",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        using var startGate = new ManualResetEventSlim(false);

        var attempts = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() =>
            {
                startGate.Wait();
                return kernel.RequestHumanInputDeduplicated(
                    goal.Id,
                    task.Id,
                    "Choose the integration scope.",
                    blockerFingerprint: "same-blocker");
            }))
            .ToArray();
        startGate.Set();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results.Select(result => result.Request.Id).Distinct());
        Assert.Equal(new HumanInputRequestCounts(1, 1), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.Equal(1, results.Count(result => result.WasCreated));
        Assert.Equal(1, results.Count(result => result.WasReused));
    }

    [Xunit.Fact]
    public void Answered_matching_blocker_is_suppressed_and_delivered_in_brief()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Reuse operator decision",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var fingerprint = HumanInputRequest.BuildWorkerResultBlockerFingerprint(
            task.Id,
            task.RequiredRole,
            "Expand scope?",
            "exact-blocker - production hook is outside scope");
        var first = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: fingerprint);
        kernel.SubmitHumanInput(first.Request.Id, "Authorize the production hook.");

        var repeated = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            " expand   SCOPE? ",
            blockerFingerprint: fingerprint);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.True(repeated.WasSuppressedByAnswer);
        Assert.Equal(first.Request.Id, repeated.Request.Id);
        Assert.Equal(new HumanInputRequestCounts(1, 0), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.NotEqual(GoalStatus.WaitingForHuman, goal.Status);
        Assert.Contains("## Resolved Human Input", brief, StringComparison.Ordinal);
        Assert.Contains("Authorize the production hook.", brief, StringComparison.Ordinal);

        var changed = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Use a different deployment target?",
            blockerFingerprint: "materially-different-blocker");
        Assert.True(changed.WasCreated);
        Assert.NotEqual(first.Request.Id, changed.Request.Id);
    }

    [Xunit.Fact]
    public void Parked_completion_is_not_treated_as_an_answered_duplicate()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Re-ask after park",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var first = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker");
        kernel.ParkGoal(goal.Id, "wait for a later round");
        kernel.UnparkGoal(goal.Id, "resume work");

        var repeated = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            " expand   SCOPE? ",
            blockerFingerprint: "unchanged-blocker");
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.True(first.Request.IsSyntheticParkedHumanWaitCompletion);
        Assert.True(repeated.WasCreated);
        Assert.NotEqual(first.Request.Id, repeated.Request.Id);
        Assert.Equal(new HumanInputRequestCounts(2, 1), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.DoesNotContain("→ Goal parked: wait for a later round", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Answered_duplicate_threshold_stops_automatic_redispatch()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Bound answered duplicate retries",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var first = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker");
        kernel.SubmitHumanInput(first.Request.Id, "Authorized.");

        for (var attempt = 1; attempt < 3; attempt++)
        {
            var suppressed = kernel.RequestHumanInputDeduplicated(
                goal.Id,
                task.Id,
                "Expand scope?",
                blockerFingerprint: "unchanged-blocker");
            Assert.True(suppressed.WasSuppressedByAnswer);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }

        var thresholdAttempt = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Expand scope?",
            blockerFingerprint: "unchanged-blocker");

        Assert.True(thresholdAttempt.WasSuppressedByAnswer);
        Assert.Equal(3, first.Request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Single(goal.Timeline.Where(item =>
            item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains("suppression threshold 3 reached", StringComparison.Ordinal)));
        Assert.Single(goal.Timeline.Where(item =>
            item.Kind == ProgressKind.DuplicateHumanInputSuppressed &&
            item.Message.Contains("repeated suppression threshold reached", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void SubmitHumanInput_resolves_historical_matching_siblings()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Resolve duplicate waits",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var original = kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");
        var snapshot = kernel.ExportSnapshot();
        var firstSnapshot = snapshot.HumanInputRequests.Single();
        var duplicateId = HumanInputRequestId.New();
        var duplicate = firstSnapshot with
        {
            Id = duplicateId.Value,
            Question = "  WHICH   branch? ",
            ResumeCommand = HumanInputRequest.BuildDefaultResumeCommand(duplicateId)
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(
            snapshot with { HumanInputRequests = [firstSnapshot, duplicate] },
            clock);

        restored.SubmitHumanInput(original.Id, "Use main.");

        var requests = restored.HumanInputRequests.OrderBy(item => item.Id.Value).ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.True(request.IsCompleted));
        Assert.Equal(original.Id, restored.GetHumanInputRequest(duplicateId).SupersededByRequestId);
        Assert.Equal("Use main.", restored.GetHumanInputRequest(duplicateId).Answer);
        Assert.Empty(restored.GetPendingHumanInput(goal.Id));
    }

    [Xunit.Fact]
    public void RequestHumanInput_five_identical_attempts_keep_one_request()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal(
            "Replay repeated dispatch blocker",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single();
        var ids = Enumerable.Range(0, 5)
            .Select(_ => kernel.RequestHumanInputDeduplicated(
                goal.Id,
                task.Id,
                "Authorize expanding scope to production hooks?",
                blockerFingerprint: "unchanged-worker-result").Request.Id)
            .ToList();

        Assert.Single(ids.Distinct());
        Assert.Equal(new HumanInputRequestCounts(1, 1), kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
        Assert.Equal(4, kernel.HumanInputRequests.Single().SuppressionCount);
    }
}

