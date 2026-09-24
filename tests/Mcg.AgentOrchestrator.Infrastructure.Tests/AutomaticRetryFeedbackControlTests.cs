using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AutomaticRetryFeedbackControlTests
{
    [Xunit.Fact]
    public void PreReviewBuildFailureAutomaticRetry_PreservesExistingFeedback()
    {
        var (kernel, goal) = SoftwareGoal("Automatic retry feedback control");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        const string staleFeedback = "existing criterion feedback";
        kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, [staleFeedback]);
        Assert.Equal([staleFeedback], kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);

        var context = FocusedPreReviewContext("build-red-sha");
        const string diagnostic = "error MSB1009: Project file does not exist.";
        string? retryMessage = null;
        var driver = MakeDriver(
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
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                Assert.Equal(developer.Id, taskId);
                retryMessage = message;
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var retriedDeveloper = kernel.GetTask(goal.Id, developer.Id);
        Assert.Contains("pre-review build repair:", retryMessage, StringComparison.Ordinal);
        Assert.Contains(diagnostic, retryMessage, StringComparison.Ordinal);
        Assert.Equal(PreReviewEvidenceDisposition.Red, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal([staleFeedback], retriedDeveloper.CriterionRetryFeedback);
        Assert.Null(retriedDeveloper.AcceptedRetryFeedback);
        Assert.Equal(RetryCause.NewSourceFinding, retriedDeveloper.PendingRetryCause);
        Assert.Equal(RetryRoundKind.Mechanical, retriedDeveloper.PendingRetryRoundKind);
    }
}
