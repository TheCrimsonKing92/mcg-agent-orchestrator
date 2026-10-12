using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class FailedGoalRetryApplicationDriverEquivalenceTests
{
    [Xunit.Fact]
    public void AdvanceOnce_CriterionRetry_RecordsFeedbackThenRetriesBeforeReadingCurrentGoal()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        const string command = "powershell.exe -Command & { while ($true) { Write-Output 'broken } }";
        const string stderr = "{\"event\":\"sandbox-prep\",\"phase\":\"launch-preflight\",\"elapsedMs\":200}\n" +
            "src/ProviderParser.cs:77: text.Contains(\"usage limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "Worker inspected the classifier and prepared a focused change.\n" +
            "powershell.exe: ParserError: The string is missing the terminator: '.";
        DispatchTask(kernel, goal, task, command);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(command, @"C:\tmp", 1, string.Empty, stderr,
                DateTimeOffset.UtcNow, ProviderFailureKind: ProviderFailureKind.RateLimit));
        var effects = new List<string>();
        FailedGoalRecoveryDecision? decision = null;
        Goal? refreshed = null;
        ConductorDriver? driver = null;
        driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            beforeFailedGoalRecoveryEffect: (_, selected) =>
            {
                decision = selected;
                Assert.Equal(FailedGoalRecoveryAction.CriterionRetry, selected.Action);
                Assert.Equal("real-worker-or-command-failure", selected.DiscriminatingEvidence);
                Assert.Null(selected.WarningMessage);
            },
            recordTaskNote: (_, _, _) => effects.Add("note"),
            recordCriterionRetryFeedback: (goalId, taskId, feedback) =>
            {
                effects.Add("feedback");
                Assert.Equal(goal.Id, goalId);
                Assert.Equal(task.Id, taskId);
                Assert.Equal(new[] { decision!.FeedbackCommand!, decision.FeedbackEvidence! }, feedback);
                return kernel.RecordCriterionRetryFeedback(goalId, taskId, feedback);
            },
            retryTaskWithCause: (goalId, taskId, reason, roundKind, cause) =>
            {
                effects.Add("retry");
                AssertRetryArguments(decision!, goalId, taskId, reason, roundKind, cause);
                // Supply a new authoritative goal object only during retry. Dispatch must see
                // that object, proving the driver reads current state after applying the retry.
                var currentKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
                var retried = currentKernel.RetryTask(goalId, taskId, reason, retryRoundKind: roundKind);
                refreshed = currentKernel.GetGoal(goalId);
                driver!.BeginTick(currentKernel, 1);
                return retried;
            },
            dispatchAndStart: current =>
            {
                effects.Add("dispatch");
                Assert.NotNull(refreshed);
                Assert.Same(refreshed, current);
                Assert.NotSame(goal, current);
                Assert.Equal(WorkTaskStatus.Assigned, current.Tasks.Single().Status);
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { "feedback", "retry", "dispatch" }, effects);
        Assert.Equal(1, refreshed!.Tasks.Single().CriterionRetryCount);
        Assert.Equal(1, refreshed.AutomaticAcceptanceRetryCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
    }

    [Xunit.Fact]
    public void AdvanceOnce_FindingRetry_AppliesPendingNoteThenWarningThenRetryWithoutFeedback()
    {
        var (kernel, goal) = SoftwareGoal();
        // Missing reviewer attestation creates a real pending note before the warning.
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Preserve retry application order",
            ["Retry effects retain their order."], VerificationClass.TestVerifiable, [], []));
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        for (var round = 1; round <= 3; round++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {round}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }
        FailReviewerNeedsWork(kernel, goal, reviewer, "Developer still misses the review blocker.");
        var effects = new List<string>();
        FailedGoalRecoveryDecision? decision = null;
        Goal? refreshed = null;
        ConductorDriver? driver = null;
        driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            beforeFailedGoalRecoveryEffect: (_, selected) =>
            {
                decision = selected;
                Assert.Equal(FailedGoalRecoveryAction.FindingRetry, selected.Action);
                Assert.Contains("auto-review-retry escalation-warning round 4/6", selected.WarningMessage,
                    StringComparison.Ordinal);
            },
            recordTaskNote: (goalId, taskId, message) =>
            {
                Assert.Equal(goal.Id, goalId);
                if (message == decision!.WarningMessage)
                {
                    effects.Add("note");
                    Assert.Equal(developer.Id, taskId);
                }
                else
                {
                    effects.Add("pending-note");
                    Assert.Equal(reviewer.Id, taskId);
                    Assert.Contains("CRITERIA_ATTESTATION missing:", message, StringComparison.Ordinal);
                }
                kernel.RecordTaskNote(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: (_, _, _) => { effects.Add("feedback"); return -1; },
            retryTaskWithCause: (goalId, taskId, reason, roundKind, cause) =>
            {
                effects.Add("retry");
                AssertRetryArguments(decision!, goalId, taskId, reason, roundKind, cause);
                var currentKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
                var retried = currentKernel.RetryTask(goalId, taskId, reason, retryRoundKind: roundKind);
                refreshed = currentKernel.GetGoal(goalId);
                driver!.BeginTick(currentKernel, 1);
                return retried;
            },
            dispatchAndStart: current =>
            {
                effects.Add("dispatch");
                Assert.NotNull(refreshed);
                Assert.Same(refreshed, current);
                Assert.NotSame(goal, current);
                Assert.Equal(WorkTaskStatus.Assigned, current.Tasks.Single(task => task.Id == developer.Id).Status);
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { "pending-note", "note", "retry", "dispatch" }, effects);
    }

    [Xunit.Fact]
    public void AdvanceOnce_ActionableRedRunningProcessConflict_PreservesEscalationDecisionAndText()
    {
        const string candidateSha = "abc1234";
        const string exceptionMessage = "Cannot retry Developer task while downstream Tester task has a running process.";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED collides with a running downstream task",
            findings: [EvidenceFindingWithRequest("Developer-owned fixture is RED.", id: "fixture-red",
                classes: ["GateReadyCandidateProjectorTests"])]);
        var effects = new List<string>();
        FailedGoalRecoveryDecision? decision = null;
        string? escalationReason = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, candidateSha),
            beforeFailedGoalRecoveryEffect: (_, selected) =>
            {
                decision = selected;
                Assert.Equal(FailedGoalRecoveryAction.FindingRetry, selected.Action);
                Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", selected.Reason, StringComparison.Ordinal);
            },
            recordCriterionRetryFeedback: (_, _, _) => { effects.Add("feedback"); return -1; },
            retryTaskWithCause: (goalId, taskId, reason, roundKind, cause) =>
            {
                effects.Add("retry");
                AssertRetryArguments(decision!, goalId, taskId, reason, roundKind, cause);
                throw new InvalidOperationException(exceptionMessage);
            },
            dispatchAndStart: _ => { effects.Add("dispatch"); return DispatchStartOutcome.Started(); },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, reason) => { effects.Add("escalate"); escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var expectedReason = $"ACTIONABLE_CANDIDATE_RED_LIFECYCLE_CONFLICT: {decision!.Reason} " +
            $"Developer retry was not applied because {exceptionMessage} No additional downstream dispatch was started.";
        Assert.Equal(expectedReason, escalated.Reason);
        Assert.Equal(expectedReason, escalationReason);
        var expectedDecision = FailedGoalRecoveryDecisionRecords.CandidateRedLifecycleConflict(decision.Identity, expectedReason);
        Assert.NotNull(escalated.Decision);
        Assert.Equal(expectedDecision.Stage, escalated.Decision.Stage);
        Assert.Equal(expectedDecision.Action, escalated.Decision.Action);
        Assert.Equal(expectedDecision.Rung, escalated.Decision.Rung);
        Assert.Equal(expectedDecision.DiscriminatingEvidence, escalated.Decision.DiscriminatingEvidence);
        Assert.Equal(expectedDecision.Reason, escalated.Decision.Reason);
        Assert.Equal(expectedDecision.Facts, escalated.Decision.Facts);
        Assert.Contains(exceptionMessage, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!).ReceiptId,
            escalated.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "retry", "escalate" }, effects);
    }

    private static void AssertRetryArguments(FailedGoalRecoveryDecision decision,
        GoalId goalId, TaskId taskId, string reason, RetryRoundKind? roundKind, RetryCause cause)
    {
        Assert.Equal(decision.Identity.GoalId, goalId);
        Assert.Equal(decision.Identity.TaskId, taskId);
        Assert.Equal(decision.Reason, reason);
        Assert.Equal(decision.RoundKind, roundKind);
        Assert.Equal(decision.RetryCause, cause);
    }
}
