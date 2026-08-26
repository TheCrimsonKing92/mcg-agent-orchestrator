using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LoopHealthReportTests
{
    private static readonly IReadOnlyList<AgentDefinition> DefaultAgents =
        AgentCatalog.Default().Agents;

    private const string WorkDir = "C:\\work";
    private const string DispatchCommand = "worker-cli run";

    // Three-goal fixture used by most tests:
    //   Goal 1 – retry goal (1 task): dispatch fails → RetryTask → dispatch passes → Goal Completed
    //   Goal 2 – false-completion goal (2 tasks): Task1 exits 0 but file-change guard fires the rejection
    //            marker in StandardError; Task2 stays pending so goal remains Active
    //   Goal 3 – clean one-shot goal (1 task): single dispatch passes → Goal Completed

    [Xunit.Fact(DisplayName = "LoopHealth_dispatches_per_successful_merge_counts_dispatch_events_over_completed_goals")]
    public void LoopHealthDispatchesPerSuccessfulMergeCountsDispatchEventsOverCompletedGoals()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // Dispatches: retry=2, false-completion=1, clean=1 → 4 total
        // Completed goals: retry goal + clean goal = 2 (false-completion goal is Active)
        Assert.Equal(4, report.TotalDispatchCount);
        Assert.Equal(2, report.CompletedGoalCount);
        Assert.Equal(2.0, report.DispatchesPerSuccessfulMerge);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_completion_catch_rate_counts_tasks_with_false_positive_rejection_marker")]
    public void LoopHealthFalseCompletionCatchRateCountsTasksWithFalsePositiveRejectionMarker()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // 4 total tasks (1+2+1); Task2 of Goal2 has no verifications → 3 tasks have any verification.
        // Only Goal2's Task1 has a verification whose StandardError contains the rejection marker → rate = 1/3.
        Assert.Equal(4, report.TotalTaskCount);
        var expectedRate = 1.0 / 3.0;
        Assert.True(Math.Abs(report.FalseCompletionCatchRate - expectedRate) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_completion_catch_rate_does_not_count_plain_pass_then_fail_without_marker")]
    public void LoopHealthFalseCompletionCatchRateDoesNotCountPlainPassThenFailWithoutMarker()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unrelated fail goal", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        var task = goal.Tasks[0];

        // First verification: passes
        RecordCompletedDispatch(kernel, goal, task, "Anthropic", "claude-sonnet-4-6");

        // Second verification: fails but NOT due to the false-positive rejection guard
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "dotnet test",
                WorkDir,
                1,
                "Test failed.",
                "Error: 3 tests failed — not a false-positive rejection",
                DateTimeOffset.UtcNow));

        var report = kernel.BuildLoopHealthReport();

        // One task has verifications; plain pass→fail without the marker must NOT be counted.
        Assert.Equal(0.0, report.FalseCompletionCatchRate);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_rework_retry_rate_counts_tasks_with_TaskRetried_timeline_events")]
    public void LoopHealthReworkRetryRateCountsTasksWithTaskRetriedTimelineEvents()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // Only Goal1's task has a TaskRetried event; 4 total tasks → rate = 1/4
        var expectedRate = 1.0 / 4.0;
        Assert.True(Math.Abs(report.ReworkRetryRate - expectedRate) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_operator_prompts_per_goal_counts_human_input_requests")]
    public void LoopHealthOperatorPromptsPerGoalCountsHumanInputRequests()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Goal A", [MakeTask()]);
        var goal2 = kernel.CreateGoal("Goal B", [MakeTask()]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);

        kernel.RequestHumanInput(goal1.Id, null, "Is this the right approach?");

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(2, report.GoalCount);
        Assert.Equal(0.5, report.OperatorPromptsPerGoal);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_median_time_to_acceptance_is_null_when_no_completed_goals")]
    public void LoopHealthMedianTimeToAcceptanceIsNullWhenNoCompletedGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Verified but not landed goal", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordVerifiedDispatch(kernel, goal, goal.Tasks[0]);

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(0, report.CompletedGoalCount);
        Assert.Equal(0.0, report.DispatchesPerSuccessfulMerge);
        Assert.True(report.MedianTimeToAcceptanceHours is null);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_median_time_to_acceptance_returns_value_for_completed_goals")]
    public void LoopHealthMedianTimeToAcceptanceReturnsValueForCompletedGoals()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        // retry goal and clean goal are both Completed; median should be non-null and >= 0
        Assert.Equal(2, report.CompletedGoalCount);
        Assert.True(report.MedianTimeToAcceptanceHours.HasValue);
        Assert.True(report.MedianTimeToAcceptanceHours!.Value >= 0.0);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_per_model_outcome_mix_delegates_to_ModelOutcomeScorecard")]
    public void LoopHealthPerModelOutcomeMixDelegatesToModelOutcomeScorecard()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal1 = kernel.CreateGoal("Feature X", [MakeTask()]);
        var goal2 = kernel.CreateGoal("Feature Y", [MakeTask()]);
        kernel.ActivateGoal(goal1.Id, DefaultAgents);
        kernel.ActivateGoal(goal2.Id, DefaultAgents);

        RecordCompletedDispatch(kernel, goal1, goal1.Tasks[0], "Anthropic", "claude-sonnet-4-6");
        RecordCompletedDispatch(kernel, goal2, goal2.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        var report = kernel.BuildLoopHealthReport();

        var record = report.ModelOutcomeMix.Single(r =>
            r.ProviderName == "Anthropic" && r.ModelName == "claude-sonnet-4-6");
        Assert.Equal(ModelOutcomeRecommendation.Prefer, record.Recommendation);
        Assert.Equal(2, record.Completed);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_lastN_window_restricts_to_most_recent_N_goals")]
    public void LoopHealthLastNWindowRestrictsToMostRecentNGoals()
    {
        var kernel = new AgentOrchestratorKernel();
        for (var i = 0; i < 5; i++)
        {
            kernel.CreateGoal($"Goal {i}", [MakeTask()]);
        }

        var allReport = kernel.BuildLoopHealthReport();
        var windowedReport = kernel.BuildLoopHealthReport(lastN: 3);

        Assert.Equal(5, allReport.GoalCount);
        Assert.Equal(3, windowedReport.GoalCount);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_empty_state_returns_zero_metrics_without_exception")]
    public void LoopHealthEmptyStateReturnsZeroMetricsWithoutException()
    {
        var kernel = new AgentOrchestratorKernel();

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(0, report.GoalCount);
        Assert.Equal(0, report.CompletedGoalCount);
        Assert.Equal(0, report.TotalTaskCount);
        Assert.Equal(0.0, report.DispatchesPerSuccessfulMerge);
        Assert.Equal(0.0, report.FalseCompletionCatchRate);
        Assert.Equal(0.0, report.OperatorPromptsPerGoal);
        Assert.Equal(0.0, report.ReworkRetryRate);
        Assert.True(report.MedianTimeToAcceptanceHours is null);
        Assert.Equal(0, report.JudgeVerdictDistributions.Count);
        Assert.Equal(0.0, report.InterJudgeAgreementRate);
        Assert.Equal(0.0, report.FalseBlockRate);
        Assert.Equal(0.0, report.FalsePassRate);
    }

    [Xunit.Fact]
    public void LoopHealthReportsPaidRetrySloFromDurableAdmissionReceipts()
    {
        var firstAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Paid retry SLO", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        var task = goal.Tasks.Single();
        var retryAt = firstAt.AddHours(1);
        var completedAt = firstAt.AddHours(2);
        var firstFingerprint = Fingerprint("candidate-a");
        var retryFingerprint = Fingerprint("candidate-b");

        kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(firstAt, firstFingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, firstFingerprint, PaidRouteClassification.Paid, firstAt);
        RecordStartedProcess(kernel, goal, task, firstAt, 1001);
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            task.LastProcess! with { CompletedAt = firstAt, ExitCode = 1 },
            verification: null);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 1, "", "source finding", firstAt));
        clock.UtcNow = retryAt;
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Repair the new source finding.",
            retryCause: RetryCause.NewSourceFinding);

        kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(retryAt, retryFingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, retryFingerprint, PaidRouteClassification.Paid, retryAt);
        RecordStartedProcess(kernel, goal, task, retryAt, 1002);
        clock.UtcNow = completedAt;
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            task.LastProcess! with { CompletedAt = completedAt, ExitCode = 0 },
            verification: null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "fixed", "", completedAt));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "later verification", "", completedAt.AddHours(1)));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        kernel.CompleteGoal(goal.Id, "Landed after verified retry.");

        var activeGoal = kernel.CreateGoal("Active retry must not enter landed numerator", [MakeTask()]);
        kernel.ActivateGoal(activeGoal.Id, DefaultAgents);
        var activeTask = activeGoal.Tasks.Single();
        var activeFirstAt = completedAt.AddHours(2);
        var activeRetryAt = activeFirstAt.AddMinutes(10);
        var activeFirstFingerprint = Fingerprint("active-candidate-a");
        var activeRetryFingerprint = Fingerprint("active-candidate-b");
        kernel.RecordTaskDispatch(activeGoal.Id, activeTask.Id, PaidDispatch(activeFirstAt, activeFirstFingerprint));
        kernel.RecordPreparedRetryAdmission(
            activeGoal.Id, activeTask.Id, activeFirstFingerprint, PaidRouteClassification.Paid, activeFirstAt);
        RecordStartedProcess(kernel, activeGoal, activeTask, activeFirstAt, 2001);
        kernel.RecordTaskProcessRefreshed(
            activeGoal.Id,
            activeTask.Id,
            activeTask.LastProcess! with { CompletedAt = activeFirstAt, ExitCode = 1 },
            verification: null);
        kernel.RecordDispatchExecutionResult(
            activeGoal.Id,
            activeTask.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 1, "", "main drift", activeFirstAt));
        clock.UtcNow = activeRetryAt;
        kernel.RetryTask(
            activeGoal.Id,
            activeTask.Id,
            "Reconcile current main.",
            retryCause: RetryCause.MainDriftConflict);
        kernel.RecordTaskDispatch(activeGoal.Id, activeTask.Id, PaidDispatch(activeRetryAt, activeRetryFingerprint));
        kernel.RecordPreparedRetryAdmission(
            activeGoal.Id, activeTask.Id, activeRetryFingerprint, PaidRouteClassification.Paid, activeRetryAt);

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(1, report.PaidRetryDispatchCount);
        Assert.Equal(1.0, report.PaidRetryDispatchesPerLandedGoal);
        Assert.Equal(0, report.SameFingerprintPreventedCount);
        Assert.Equal(1, report.RetryCauseDistribution.Single(item => item.Cause == RetryCause.NewSourceFinding).Count);
        Assert.Equal(2.0, report.MedianRetryResolutionHours);
        var developer = report.FirstPassCompletionByRole.Single(item => item.Role == AgentRole.Developer);
        Assert.Equal(0, developer.FirstPassCompletedCount);
        Assert.Equal(2, developer.PresentTaskCount);
        Assert.Equal(0.0, developer.CompletionRate);
        Assert.Null(report.FirstPassCompletionByRole.Single(item => item.Role == AgentRole.Tester).CompletionRate);
        Assert.Equal(0, report.RetryFingerprintUnavailableCount);
        Assert.Equal(0, report.RetryPaidAuthorityUnknownCount);
    }

    [Xunit.Fact]
    public void LoopHealthLegacyRetryAuthorityRemainsUnavailable()
    {
        var (kernel, _, _, _) = BuildFixture();

        var report = kernel.BuildLoopHealthReport();

        Assert.True(report.RetryFingerprintUnavailableCount > 0);
        Assert.True(report.RetryPaidAuthorityUnknownCount > 0);
        Assert.True(report.RetryCauseUnavailableCount > 0);
        Assert.True(report.RetryCauseDistribution.Single(item => item.Cause == RetryCause.Unknown).Count > 0);
    }

    [Xunit.Fact]
    public void PaidRetryDispatchCountUsesTaskAndTimestampAsAttemptIdentity()
    {
        var firstAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        var retryAt = firstAt.AddHours(1);
        var completedAt = retryAt.AddHours(1);
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Count same-batch paid retries", [MakeTask(), MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);

        foreach (var task in goal.Tasks)
        {
            var firstFingerprint = Fingerprint($"first-{task.Id.Value}");
            var retryFingerprint = Fingerprint($"retry-{task.Id.Value}");
            kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(firstAt, firstFingerprint));
            kernel.RecordPreparedRetryAdmission(
                goal.Id, task.Id, firstFingerprint, PaidRouteClassification.Paid, firstAt);
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(DispatchCommand, WorkDir, 1, "", "first attempt failed", firstAt));
            clock.UtcNow = retryAt;
            kernel.RetryTask(
                goal.Id,
                task.Id,
                "Repair the blocking source finding.",
                invalidateDownstream: false,
                retryCause: RetryCause.NewSourceFinding);
            kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(retryAt, retryFingerprint));
            kernel.RecordPreparedRetryAdmission(
                goal.Id, task.Id, retryFingerprint, PaidRouteClassification.Paid, retryAt);
            RecordStartedProcess(kernel, goal, task, retryAt, task == goal.Tasks[0] ? 3001 : 3002);
            clock.UtcNow = completedAt;
            kernel.RecordTaskProcessRefreshed(
                goal.Id,
                task.Id,
                task.LastProcess! with { CompletedAt = completedAt, ExitCode = 0 },
                verification: null);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "fixed", "", completedAt));
        }

        kernel.CompleteGoal(goal.Id, "Landed after both retries succeeded.");

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(2, report.PaidRetryDispatchCount);
        Assert.Equal(2.0, report.PaidRetryDispatchesPerLandedGoal);
    }

    [Xunit.Fact]
    public void RetryResolution_TerminalBeforeSuccess_UsesTerminal()
    {
        var (kernel, goal, task, clock, firstAt) = BuildRetryResolutionFixture();

        clock.UtcNow = firstAt.AddHours(2);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Retry ended without success.");
        clock.UtcNow = firstAt.AddHours(3);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "late success", "", clock.UtcNow));

        Assert.Equal(2.0, kernel.BuildLoopHealthReport().MedianRetryResolutionHours);
    }

    [Xunit.Fact]
    public void RetryResolution_SuccessBeforeTerminal_UsesSuccess()
    {
        var (kernel, goal, task, clock, firstAt) = BuildRetryResolutionFixture();

        clock.UtcNow = firstAt.AddHours(2);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "success", "", clock.UtcNow));
        clock.UtcNow = firstAt.AddHours(3);
        kernel.CancelGoal(goal.Id, "Terminal activity after retry resolution.");

        Assert.Equal(2.0, kernel.BuildLoopHealthReport().MedianRetryResolutionHours);
    }

    [Xunit.Fact]
    public void RetryResolution_WithoutResolution_IsUnavailable()
    {
        var (kernel, _, _, _, _) = BuildRetryResolutionFixture();

        Assert.Null(kernel.BuildLoopHealthReport().MedianRetryResolutionHours);
    }

    [Xunit.Fact]
    public void RetryResolution_PreAdmissionSuccess_DoesNotResolveRetryInterval()
    {
        var (kernel, goal, task, clock, firstAt) = BuildRetryResolutionFixture();

        clock.UtcNow = firstAt.AddMinutes(30);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "pre-admission success", "", clock.UtcNow));

        Assert.Null(kernel.BuildLoopHealthReport().MedianRetryResolutionHours);
    }

    [Xunit.Fact]
    public void RetryResolution_RetriedFailureDoesNotEndResolutionChain()
    {
        var (kernel, goal, task, clock, firstAt) = BuildRetryResolutionFixture();

        clock.UtcNow = firstAt.AddHours(2);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "The first retry failed.");
        clock.UtcNow = firstAt.AddHours(3);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Retry the still-unresolved source finding.",
            invalidateDownstream: false,
            retryCause: RetryCause.NewSourceFinding);
        var finalFingerprint = Fingerprint("candidate-c");
        kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(clock.UtcNow, finalFingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, finalFingerprint, PaidRouteClassification.Paid, clock.UtcNow);
        clock.UtcNow = firstAt.AddHours(5);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 0, "eventual success", "", clock.UtcNow));

        Assert.Equal(5.0, kernel.BuildLoopHealthReport().MedianRetryResolutionHours);
    }

    // --- Semantic-acceptance judge agreement tests ---

    [Xunit.Fact(DisplayName = "LoopHealth_judge_verdict_distribution_counts_met_not_met_and_no_verdict_per_judge")]
    public void LoopHealthJudgeVerdictDistributionCountsMetNotMetAndNoVerdictPerJudge()
    {
        var receipts = new[]
        {
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow,
                "goal-1",
                Consensus: true,
                Judges:
                [
                    new SemanticJudgeReceiptEntry("judge-a", Valid: true,  CriteriaMet: true),
                    new SemanticJudgeReceiptEntry("judge-b", Valid: false, CriteriaMet: false)
                ]),
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow,
                "goal-2",
                Consensus: false,
                Judges:
                [
                    new SemanticJudgeReceiptEntry("judge-a", Valid: true,  CriteriaMet: false)
                ])
        };

        var report = LoopHealthReport.Build([], [], receipts: receipts);

        var a = report.JudgeVerdictDistributions.Single(d => d.JudgeName == "judge-a");
        var b = report.JudgeVerdictDistributions.Single(d => d.JudgeName == "judge-b");

        Assert.Equal(1, a.MetCount);
        Assert.Equal(1, a.NotMetCount);
        Assert.Equal(0, a.NoVerdictCount);

        Assert.Equal(0, b.MetCount);
        Assert.Equal(0, b.NotMetCount);
        Assert.Equal(1, b.NoVerdictCount);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_inter_judge_agreement_rate_is_fraction_of_receipts_where_all_valid_judges_agree")]
    public void LoopHealthInterJudgeAgreementRateIsFractionOfReceiptsWhereAllValidJudgesAgree()
    {
        var receipts = new[]
        {
            // Both judges agree: MET
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, "g1", Consensus: true,
                Judges:
                [
                    new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: true),
                    new SemanticJudgeReceiptEntry("b", Valid: true, CriteriaMet: true)
                ]),
            // Judges disagree: a=MET, b=NOT-MET
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, "g2", Consensus: null,
                Judges:
                [
                    new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: true),
                    new SemanticJudgeReceiptEntry("b", Valid: true, CriteriaMet: false)
                ]),
            // Only one valid judge — excluded from denominator
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, "g3", Consensus: false,
                Judges:
                [
                    new SemanticJudgeReceiptEntry("a", Valid: true,  CriteriaMet: false),
                    new SemanticJudgeReceiptEntry("b", Valid: false, CriteriaMet: false)
                ])
        };

        var report = LoopHealthReport.Build([], [], receipts: receipts);

        // 2 receipts with 2+ valid judges; 1 agrees → 0.5
        Assert.True(Math.Abs(report.InterJudgeAgreementRate - 0.5) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_inter_judge_agreement_rate_is_zero_when_no_receipt_has_multiple_valid_judges")]
    public void LoopHealthInterJudgeAgreementRateIsZeroWhenNoReceiptHasMultipleValidJudges()
    {
        var receipts = new[]
        {
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, "g1", Consensus: true,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: true)])
        };

        var report = LoopHealthReport.Build([], [], receipts: receipts);

        Assert.Equal(0.0, report.InterJudgeAgreementRate);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_block_rate_counts_consensus_not_met_for_completed_goals")]
    public void LoopHealthFalseBlockRateCountsConsensusNotMetForCompletedGoals()
    {
        var kernel = new AgentOrchestratorKernel();

        var completedGoal = kernel.CreateGoal("Completed goal", [MakeTask()]);
        kernel.ActivateGoal(completedGoal.Id, DefaultAgents);
        RecordCompletedDispatch(kernel, completedGoal, completedGoal.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        var cancelledGoal = kernel.CreateGoal("Cancelled goal", [MakeTask()]);
        kernel.ActivateGoal(cancelledGoal.Id, DefaultAgents);
        kernel.CancelGoal(cancelledGoal.Id, "Abandoned by operator.");

        // consensus=false for the completed goal → false-block
        // consensus=false for the cancelled goal → not a false-block
        var receipts = new[]
        {
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, completedGoal.Id.Value, Consensus: false,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: false)]),
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, cancelledGoal.Id.Value, Consensus: false,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: false)])
        };

        var report = LoopHealthReport.Build(kernel.Goals, kernel.HumanInputRequests, receipts: receipts);

        // 1 false-block out of 2 terminal receipts
        Assert.True(Math.Abs(report.FalseBlockRate - 0.5) < 0.01);
        Assert.Equal(0.0, report.FalsePassRate);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_verified_goal_is_not_counted_as_completed")]
    public void LoopHealthVerifiedGoalIsNotCountedAsCompleted()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Verified but unmerged", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        RecordVerifiedDispatch(kernel, goal, goal.Tasks[0]);

        var report = kernel.BuildLoopHealthReport();

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(1, report.GoalCount);
        Assert.Equal(0, report.CompletedGoalCount);
        Assert.Equal(0.0, report.DispatchesPerSuccessfulMerge);
        Assert.True(report.MedianTimeToAcceptanceHours is null);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_false_pass_rate_counts_consensus_met_for_failed_or_cancelled_goals")]
    public void LoopHealthFalsePassRateCountsConsensusMetForFailedOrCancelledGoals()
    {
        var kernel = new AgentOrchestratorKernel();

        var completedGoal = kernel.CreateGoal("Completed goal", [MakeTask()]);
        kernel.ActivateGoal(completedGoal.Id, DefaultAgents);
        RecordCompletedDispatch(kernel, completedGoal, completedGoal.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        var cancelledGoal = kernel.CreateGoal("Cancelled goal", [MakeTask()]);
        kernel.ActivateGoal(cancelledGoal.Id, DefaultAgents);
        kernel.CancelGoal(cancelledGoal.Id, "Abandoned by operator.");

        // consensus=true for completed → true positive (no error)
        // consensus=true for cancelled → false-pass
        var receipts = new[]
        {
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, completedGoal.Id.Value, Consensus: true,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: true)]),
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, cancelledGoal.Id.Value, Consensus: true,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: true)])
        };

        var report = LoopHealthReport.Build(kernel.Goals, kernel.HumanInputRequests, receipts: receipts);

        // 1 false-pass out of 2 terminal receipts
        Assert.Equal(0.0, report.FalseBlockRate);
        Assert.True(Math.Abs(report.FalsePassRate - 0.5) < 0.01);
    }

    [Xunit.Fact(DisplayName = "LoopHealth_active_goals_excluded_from_false_block_and_false_pass_denominator")]
    public void LoopHealthActiveGoalsExcludedFromFalseBlockAndFalsePassDenominator()
    {
        var kernel = new AgentOrchestratorKernel();
        var activeGoal = kernel.CreateGoal("Active goal", [MakeTask()]);
        kernel.ActivateGoal(activeGoal.Id, DefaultAgents);

        var receipts = new[]
        {
            new SemanticAcceptanceReceipt(
                DateTimeOffset.UtcNow, activeGoal.Id.Value, Consensus: false,
                Judges: [new SemanticJudgeReceiptEntry("a", Valid: true, CriteriaMet: false)])
        };

        var report = LoopHealthReport.Build(kernel.Goals, kernel.HumanInputRequests, receipts: receipts);

        // Active goal is not terminal; denominator = 0 → both rates are 0.0
        Assert.Equal(0.0, report.FalseBlockRate);
        Assert.Equal(0.0, report.FalsePassRate);
    }

    // Builds the canonical three-goal fixture.
    // Goal 1 (1 task)  – retry: dispatch fails, RetryTask, dispatch passes → Completed
    // Goal 2 (2 tasks) – false-completion: Task1 exits 0, file-change guard fires rejection marker;
    //                    Task2 stays pending so goal remains Active
    // Goal 3 (1 task)  – clean one-shot: dispatch passes → Completed
    private static (AgentOrchestratorKernel, Goal RetryGoal, Goal FalseCompletionGoal, Goal CleanGoal) BuildFixture()
    {
        var kernel = new AgentOrchestratorKernel();

        // Goal 1: retry goal
        var retryGoal = kernel.CreateGoal("Retry goal", [MakeTask()]);
        kernel.ActivateGoal(retryGoal.Id, DefaultAgents);
        var retryTask = retryGoal.Tasks[0];
        RecordFailedDispatch(kernel, retryGoal, retryTask);
        kernel.RetryTask(retryGoal.Id, retryTask.Id, "Retrying after build failure.");
        RecordCompletedDispatch(kernel, retryGoal, retryTask, "Anthropic", "claude-sonnet-4-6");

        // Goal 2: false-completion goal — two tasks so goal stays Active while Task1 has a caught false completion
        var fcGoal = kernel.CreateGoal("False-completion goal", [MakeTask(), MakeTask()]);
        kernel.ActivateGoal(fcGoal.Id, DefaultAgents);
        var fcTask1 = fcGoal.Tasks[0];
        RecordCompletedDispatch(kernel, fcGoal, fcTask1, "Anthropic", "claude-sonnet-4-6");
        // False-positive rejection: the file-change guard fires and emits the rejection marker in StandardError.
        kernel.RecordTaskVerification(
            fcGoal.Id,
            fcTask1.Id,
            new TaskVerificationRecord(
                "worker-cli run",
                WorkDir,
                1,
                string.Empty,
                "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence. Blocking completion.",
                DateTimeOffset.UtcNow));

        // Goal 3: clean one-shot
        var cleanGoal = kernel.CreateGoal("Clean goal", [MakeTask()]);
        kernel.ActivateGoal(cleanGoal.Id, DefaultAgents);
        RecordCompletedDispatch(kernel, cleanGoal, cleanGoal.Tasks[0], "Anthropic", "claude-sonnet-4-6");

        return (kernel, retryGoal, fcGoal, cleanGoal);
    }

    private static TaskSpec MakeTask() =>
        new(TaskId.New(), "Developer implementation task", AgentRole.Developer);

    private static RetryContextFingerprint Fingerprint(string candidate) =>
        RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
            "goal", "task", AgentRole.Developer, "Anthropic", "claude-sonnet-4-6",
            PaidRouteClassification.Paid, candidate, "criteria", [], [], [], [], "base", "main"));

    private static (
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task,
        MutableClock Clock,
        DateTimeOffset FirstAt) BuildRetryResolutionFixture()
    {
        var firstAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Retry resolution goal", [MakeTask()]);
        kernel.ActivateGoal(goal.Id, DefaultAgents);
        var task = goal.Tasks[0];
        var firstFingerprint = Fingerprint("candidate-a");
        var retryFingerprint = Fingerprint("candidate-b");

        kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(firstAt, firstFingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, firstFingerprint, PaidRouteClassification.Paid, firstAt);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(DispatchCommand, WorkDir, 1, "", "first attempt failed", firstAt));

        clock.UtcNow = firstAt.AddHours(1);
        kernel.RetryTask(
            goal.Id,
            task.Id,
            "Address the new source finding.",
            invalidateDownstream: false,
            retryCause: RetryCause.NewSourceFinding);
        kernel.RecordTaskDispatch(goal.Id, task.Id, PaidDispatch(clock.UtcNow, retryFingerprint));
        kernel.RecordPreparedRetryAdmission(
            goal.Id, task.Id, retryFingerprint, PaidRouteClassification.Paid, clock.UtcNow);

        return (kernel, goal, task, clock, firstAt);
    }

    private static TaskDispatchRecord PaidDispatch(
        DateTimeOffset dispatchedAt,
        RetryContextFingerprint fingerprint) =>
        new(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            dispatchedAt,
            ProviderName: "Anthropic",
            ModelName: "claude-sonnet-4-6",
            RetryContextFingerprint: fingerprint,
            PaidRoute: PaidRouteClassification.Paid);

    private static void RecordStartedProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset startedAt,
        int processId) =>
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                processId, DispatchCommand, WorkDir, "out.log", "err.log", "exit.txt",
                startedAt, CompletedAt: null, ExitCode: null));

    private static void RecordCompletedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string providerName = "Anthropic",
        string modelName = "claude-sonnet-4-6")
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow,
            ProviderName: providerName,
            ModelName: modelName);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            0,
            "Task completed successfully.",
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        if (goal.Status == GoalStatus.Verified)
        {
            kernel.CompleteGoal(goal.Id, "Completed after durable integration and cleanup evidence.");
        }
    }

    private static void RecordVerifiedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string providerName = "Anthropic",
        string modelName = "claude-sonnet-4-6")
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow,
            ProviderName: providerName,
            ModelName: modelName);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            0,
            "Task completed successfully.",
            string.Empty,
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static void RecordFailedDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task)
    {
        var dispatch = new TaskDispatchRecord(
            "worker-cli",
            DispatchCommand,
            WorkDir,
            DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var verification = new TaskVerificationRecord(
            DispatchCommand,
            WorkDir,
            1,
            "Task failed.",
            "Error: build failed",
            DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
