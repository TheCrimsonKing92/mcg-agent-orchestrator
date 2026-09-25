using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsUnchangedCandidate
{
    [Xunit.Fact]
    public void SameCandidateHoldsWithTypedReasonWithoutDispatch()
    {
        var (goal, task, identity) = RetriedGoal();
        var calls = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { calls++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => identity);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Equal(0, calls);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(task.RequiredRole, Xunit.Assert.IsType<UnchangedCandidateHoldReason>(held.TypedReason).Role);
        Xunit.Assert.Contains("verdict=passed", held.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ChangedCandidateDispatchesAsBefore()
    {
        var (goal, _, _) = RetriedGoal();
        var calls = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { calls++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => new CandidateIdentity("different", "base", "manifest"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Equal(1, calls);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    private static (Goal Goal, TaskSpec Task, CandidateIdentity Identity) RetriedGoal()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        var task = Xunit.Assert.Single(goal.Tasks);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        ConductorDriverTests.DispatchTask(kernel, goal, task);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, task.Id, "retry without new input", RetryCause.UnchangedContextRepeat);
        return (goal, task, identity);
    }
}
