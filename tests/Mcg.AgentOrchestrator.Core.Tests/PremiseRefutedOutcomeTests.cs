using Mcg.AgentOrchestrator.Core;

public sealed class PremiseRefutedOutcomeTests
{
    [Xunit.Theory]
    [Xunit.InlineData(FailedGoalStaleRecoveryDisposition.None)]
    [Xunit.InlineData(FailedGoalStaleRecoveryDisposition.Retry)]
    public void PremiseFindingEscalatesWithoutRetryEvenWithAutoRetryRecommendation(FailedGoalStaleRecoveryDisposition stale)
    {
        var task = new FailedGoalRecoveryTaskFacts(
            new TaskId("task-policy"), AgentRole.Developer, WorkTaskStatus.Failed,
            HasLiveProcess: false, IsExitedWithoutAppliedCompletion: false, AttemptIdentity: "process:7:42",
            DispatchOutcomeKind.UnknownFailure, RecoveryRecommendation.AutoRetry, TaskOutcomeClass.Finding,
            EvidenceSummary: "evidence", stale, StaleRecoveryDiagnostic: "stale diagnostic",
            EmptyOutputRetryCount: 0, CriterionRetryCount: 0, AutomaticRetryCause: null,
            ProviderFailureKind: null, ExitCode: 1, Command: "verify", RetryBackoff: TimeSpan.Zero);
        var facts = new FailedGoalRecoveryFacts(new GoalId("goal-policy"), GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: 0, maxCriterionRetries: 2, maxTransientAttempts: 2,
            contextVersion: "context-v1", [task], terminalEscalationReason: "terminal failure");
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal("premise-refuted-awaiting-operator", decision.DiscriminatingEvidence);
        Assert.Null(decision.RetryCause);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, false)]
    [Xunit.InlineData(AgentRole.Developer, null)]
    [Xunit.InlineData(AgentRole.Developer, true)]
    [Xunit.InlineData(AgentRole.Tester, false)]
    [Xunit.InlineData(AgentRole.Tester, null)]
    [Xunit.InlineData(AgentRole.Tester, true)]
    public void CanonicalPremiseEvidenceIsFindingRegardlessOfScope(AgentRole role, bool? scope)
    {
        var task = new TaskSpec(TaskId.New(), "Inspect disputed premise", role);
        var outcome = DispatchFailureClassifier.Classify(task, Verification(scope));

        Assert.Equal("premise-refuted", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.Finding, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal("finding", TaskOutcomeClassifier.FormatClass(outcome.OutcomeClass));
        Assert.Equal(TaskOutcomeClass.Finding, TaskOutcomeClassifier.ParseClass("finding"));
    }

    [Xunit.Fact]
    public void IncompleteDeveloperWithoutPremiseEvidenceStillRetriesAsRealFailure()
    {
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var outcome = DispatchFailureClassifier.Classify(task, Verification(false, "none"));

        Assert.Equal("incomplete-scope-declaration", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, "incomplete-scope-declaration", RecoveryRecommendation.AutoRetry)]
    [Xunit.InlineData(AgentRole.Tester, "tester-worker-result-blocker", RecoveryRecommendation.OperatorNeeded)]
    public void FailedExitWithPremiseEvidenceRetainsFailure(AgentRole role, string rule,
        RecoveryRecommendation recovery)
    {
        var verification = Verification(false) with { ExitCode = 1 };
        var outcome = DispatchFailureClassifier.Classify(new TaskSpec(TaskId.New(), "Inspect", role), verification);

        Assert.Equal(rule, TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(recovery, outcome.RecoveryRecommendation);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, "premise-invalid", "unknown-failure")]
    [Xunit.InlineData(AgentRole.Developer, "premise-invalid - ", "unknown-failure")]
    [Xunit.InlineData(AgentRole.Tester, "premise-invalid", "tester-worker-result-blocker")]
    [Xunit.InlineData(AgentRole.Tester, "premise-invalid - ", "tester-worker-result-blocker")]
    public void MissingPremiseEvidenceRemainsMalformed(AgentRole role, string blocker, string rule)
    {
        var verification = Verification(null, blocker);
        Assert.True(WorkerResultBlockers.TryFindMalformedEvidenceBoundOutcome(verification.StandardOutput, out _));
        var outcome = DispatchFailureClassifier.Classify(new TaskSpec(TaskId.New(), "Inspect", role), verification);
        Assert.Equal(rule, TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.NotEqual(TaskOutcomeClass.Finding, outcome.OutcomeClass);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, "fail - defect", "succeeded-worker-result-failing-tests")]
    [Xunit.InlineData(AgentRole.Tester, "fail - defect", "succeeded-worker-result-failing-tests")]
    [Xunit.InlineData(AgentRole.Tester, "inconclusive - missing receipt", "tester-verification-inconclusive")]
    public void EarlierFailureEvidenceKeepsPrecedence(AgentRole role, string tests, string rule)
    {
        var outcome = DispatchFailureClassifier.Classify(new TaskSpec(TaskId.New(), "Inspect", role), Verification(false, tests: tests));
        Assert.Equal(rule, TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.NotEqual(TaskOutcomeClass.Finding, outcome.OutcomeClass);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void RecordingDeduplicatesAndReportsFindingWithoutSuccessOrFailure(AgentRole role)
    {
        var clock = new TestClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Inspect disputed premise", role);
        var goal = kernel.CreateGoal("Disputed API premise", [task]);
        var agent = new AgentDefinition(new AgentId("worker"), "worker", role,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "inspect", "C:\\repo", clock.UtcNow,
            ProviderName: "OpenAI", ModelName: "test", TaskComplexity: TaskComplexity.Simple));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var verification = Verification(false) with { CompletedAt = clock.UtcNow };
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.StartsWith($"{role} reported premise-invalid:", request.Question, StringComparison.Ordinal);
        Assert.Contains("src/Api.cs", request.Question, StringComparison.Ordinal);
        Assert.Contains("Clarify, supersede, or abandon", request.Question, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification with { CompletedAt = clock.UtcNow });
        Assert.Same(request, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));
        Assert.Equal(1, request.SuppressionCount);
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Kind is ProgressKind.TaskFailed or ProgressKind.TaskCompleted or ProgressKind.TaskRetried);
        var classification = TaskOutcomeClassifier.FromTimeline(goal.Timeline, task.Id, task.Status);
        Assert.Equal("premise-refuted", classification.Rule);
        Assert.Equal(TaskOutcomeClass.Finding, classification.Class);

        var scorecard = Assert.Single(ModelOutcomeScorecard.Build(kernel.Goals));
        Assert.Equal(1, scorecard.Findings);
        Assert.Equal(0, scorecard.Completed);
        Assert.Equal(0, scorecard.Failed);
        Assert.Equal(0, scorecard.RealFailures);
        Assert.Equal(0, scorecard.Divergence);
        Assert.Equal(0, scorecard.ClassMismatchFailures);
        var duration = Assert.Single(TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals));
        Assert.Equal(1, duration.AttemptCount);
        Assert.Equal(1, duration.FindingAttemptCount);
        Assert.Equal(0, duration.FailedAttemptCount);
        Assert.Equal(0, duration.RealFailureAttemptCount);
        Assert.Equal(0, duration.FailureRate);
        Assert.Null(duration.MedianLegitimateRuntime);
        Assert.Equal(TimeSpan.Zero, duration.MedianFailureInterventionOverhead);
        var trend = Assert.Single(TaskDurationReport.BuildDailyTrend(kernel.Goals));
        Assert.Equal(1, trend.FindingAttemptCount);
        Assert.Equal(0, trend.FailedAttemptCount);
        Assert.Equal(0, trend.FailureRate);
    }

    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Failed)]
    [Xunit.InlineData(WorkTaskStatus.WaitingForHuman)]
    public void FindingRowsRemainInScorecardDenominator(WorkTaskStatus status)
    {
        var at = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
        var finding = new ModelFitHistoryRow("goal", "finding", AgentRole.Developer, "OpenAI", "test",
            TaskComplexity.Simple, null, status, ModelFitHistory.Adequate, at,
            "premise-refuted", TaskOutcomeClass.Finding);
        var success = finding with { TaskId = "success", Outcome = WorkTaskStatus.Completed,
            OutcomeClass = TaskOutcomeClass.Success, OutcomeRule = null, Timestamp = at.AddMinutes(-1) };
        var scorecard = Assert.Single(ModelOutcomeScorecard.Build([finding, success]));
        Assert.Equal(1, scorecard.Findings);
        Assert.Equal(1, scorecard.Completed);
        Assert.Equal(0, scorecard.Failed);
        Assert.Equal(0, scorecard.Divergence);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, scorecard.Recommendation);
        Assert.DoesNotContain("Insufficient samples", scorecard.Reason, StringComparison.Ordinal);
    }

    private static TaskVerificationRecord Verification(bool? scope, string blocker = "premise-invalid - required API absent; see src/Api.cs",
        string tests = "deferred - ApiTests") => new("inspect", "C:\\repo", 0,
            $"WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: {tests}\ncommit: none\nblockers: {blocker}\n" +
            (scope is null ? "" : $"assigned_scope_complete: {scope.Value.ToString().ToLowerInvariant()}\n") +
            "model_fit: OpenAI/test - adequate - inspection - sufficient\nskills: none\nconfidence: high\nEND_WORKER_RESULT",
            "", DateTimeOffset.Parse("2026-09-21T12:01:00Z"), WorkerResultPresent: true, AssignedScopeComplete: scope);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
    }
}
