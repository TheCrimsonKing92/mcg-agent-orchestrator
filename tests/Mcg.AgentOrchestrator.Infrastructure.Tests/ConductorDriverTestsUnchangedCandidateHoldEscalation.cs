using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsUnchangedCandidateHoldEscalation
{
    [Xunit.Fact]
    public void PassedCandidateEscalatesOnceAtConfiguredTickAndStillHoldsAfterRestart()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        var task = Xunit.Assert.Single(goal.Tasks);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        ConductorDriverTests.DispatchTask(kernel, goal, task);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow,
            WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, task.Id, "repeat without new input", RetryCause.UnchangedContextRepeat);

        var dispatches = 0;
        var escalations = new List<string>();
        ConductorDriver MakeDriver()
        {
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
                recordTaskNote: (goalId, taskId, note) => kernel.RecordTaskNote(goalId, taskId, note),
                writeEscalation: (_, _, reason) => escalations.Add(reason));
            driver.OverrideCandidateIdentityResolverForTests(_ => identity);
            driver.UnchangedCandidateHoldEscalationTicks = 3;
            return driver;
        }

        var first = MakeDriver();
        for (var tick = 1; tick <= 5; tick++)
        {
            var result = first.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Xunit.Assert.Contains("verdict=passed", held.Reason, StringComparison.Ordinal);
            Xunit.Assert.Equal(tick < 3 ? 0 : 1, escalations.Count);
        }
        Xunit.Assert.Equal(0, dispatches);
        Xunit.Assert.Contains("UNCHANGED_CANDIDATE", escalations[0], StringComparison.Ordinal);
        Xunit.Assert.Contains("consecutive_holds=3", escalations[0], StringComparison.Ordinal);
        Xunit.Assert.Contains("close the task mechanically", escalations[0], StringComparison.Ordinal);
        Xunit.Assert.Contains("retry this role", escalations[0], StringComparison.Ordinal);

        var afterRestart = MakeDriver();
        for (var tick = 0; tick < 3; tick++)
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(
                afterRestart.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Xunit.Assert.Single(escalations);
        Xunit.Assert.Equal(0, dispatches);
    }
}
