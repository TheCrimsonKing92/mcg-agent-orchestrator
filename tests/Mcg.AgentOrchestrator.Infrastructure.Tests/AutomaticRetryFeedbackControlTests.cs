using Mcg.AgentOrchestrator.Core;

using static ConductorDriverTests;

public sealed class AutomaticRetryFeedbackControlTests
{
    [Xunit.Fact]
    public void NonStructuralAutomaticRetry_PreservesExistingFeedback()
    {
        var (kernel, goal) = SoftwareGoal("Automatic retry feedback control");
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != developer.Id).Append(developer))
        {
            PassVerification(kernel, goal, task);
        }

        const string staleFeedback = "existing criterion feedback";
        kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, [staleFeedback]);
        Assert.Equal([staleFeedback], kernel.GetTask(goal.Id, developer.Id).CriterionRetryFeedback);

        kernel.RetryTaskAutomatically(goal.Id, developer.Id, "pre-review build repair: focused build failed",
            RetryCause.NewSourceFinding, retryRoundKind: RetryRoundKind.Mechanical);

        var retriedDeveloper = kernel.GetTask(goal.Id, developer.Id);
        Assert.Equal([staleFeedback], retriedDeveloper.CriterionRetryFeedback);
        Assert.Null(retriedDeveloper.AcceptedRetryFeedback);
        Assert.Equal(RetryCause.NewSourceFinding, retriedDeveloper.PendingRetryCause);
        Assert.Equal(RetryRoundKind.Mechanical, retriedDeveloper.PendingRetryRoundKind);
    }
}
