using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorDriverTestsPreReviewBuildFailureRouting
{
    [Xunit.Fact]
    public void PreReview_IdenticalBuildFailure_EscalatesWithoutRedispatch()
    {
        var (kernel, goal) = ConductorDriverTests.SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            ConductorDriverTests.PassVerification(kernel, goal, task);
        }

        const string sha = "unchanged-build-red-sha";
        const string diagnostic = "error MSB1009: Project file does not exist.";
        var context = ConductorDriverTests.FocusedPreReviewContext(sha);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value,
            1,
            sha,
            context.SelectedFocusedTests,
            PreReviewEvidenceDisposition.Red,
            0,
            1,
            [new PreReviewEvidenceCheckReceipt("focused build", context.SelectedFocusedTests[0], false, 1)],
            [],
            context.MappingReason,
            null,
            DateTimeOffset.UtcNow));
        var retriedTaskIds = new List<TaskId>();
        string? escalation = null;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => context,
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "build failed before tests executed",
                Checks: [new AcceptanceCheckResult("focused build", false, 1, diagnostic)]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Empty(retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Contains("PRE_REVIEW_RED_UNCHANGED_CANDIDATE", escalation, StringComparison.Ordinal);
        Assert.Contains(sha, escalation, StringComparison.Ordinal);
        Assert.Contains(diagnostic, escalation, StringComparison.Ordinal);
    }
}
