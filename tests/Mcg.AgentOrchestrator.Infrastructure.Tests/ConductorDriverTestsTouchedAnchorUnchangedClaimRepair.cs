using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTouchedAnchorUnchangedClaimRepair
{
    [Xunit.Fact]
    public void TouchedAnchorClaimedUnchanged_RetriesReviewerWithoutAdvancingReviewRound()
    {
        const string violationCode = "ERR_REVIEW_FINDING_TOUCHED_ANCHOR_CLAIMED_UNCHANGED";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        PassVerification(kernel, goal, developer);
        PassVerification(kernel, goal, tester);
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var open = new ReviewFinding("F-1", ReviewFindingState.Open, anchor,
            "Guard remains advisory.", FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open", "C:\\tmp", 0, ReviewerPassWithFinding(open), string.Empty,
            DateTimeOffset.UtcNow, WorkerResultPresent: true));
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(open, Assert.Single(reviewer.LastVerification.MergedReviewFindings!));

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck current candidate");
        DispatchTask(kernel, goal, reviewer, "review-unchanged", reviewFindingTouchedAnchors: [anchor]);
        var submitted = open with
        {
            Description = "REVIEW DEFECT: unchanged since the earlier reviewed abc1234 candidate"
        };
        var roundBefore = ReviewRetryCapReceipt.Create(goal, 7).Round;
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-unchanged", "C:\\tmp", 0, ReviewerPassWithFinding(submitted), string.Empty,
            DateTimeOffset.UtcNow, WorkerResultPresent: true));

        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(violationCode, violation.Code);
        Assert.Equal(open.StableId, violation.PriorStableId);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification.MergedReviewFindings);
        Assert.Equal(roundBefore, ReviewRetryCapReceipt.Create(goal, 7).Round);

        TaskId? retriedTaskId = null;
        RetryRoundKind? retryRoundKind = null;
        string? retryMessage = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryRoundKind = roundKind;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Contains(violationCode, retryMessage, StringComparison.Ordinal);
        Assert.Null(escalation);
        Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.Ordinal));
        Assert.Equal(roundBefore, ReviewRetryCapReceipt.Create(goal, 7).Round);
    }
}
