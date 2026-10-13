using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsSubscriptionTerminalContract : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_subscription_Reviewer_introduction_requires_terminal_contract")]
    public void BackgroundDispatchRunnerSubscriptionReviewerIntroductionRequiresTerminalContract()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Reviewer,
        "Reviewed implementation evidence.",
        string.Empty,
        clock,
        worktree => File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
    Assert.DoesNotContain(task.VerificationHistory, verification => verification.CompletionVerdictVerifiedSuccess);
    Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
}
}
