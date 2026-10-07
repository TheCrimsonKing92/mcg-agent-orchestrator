using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the support fixture owns a unique repository, worktree, and log directory.
public sealed class PlannerDispatchCompletionTestsContractDiagnostic : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void MissingTargetCitationAppearsInTaskFailedMessageBeforeCommand()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        var plan = PlannerContractPlanFixture().Replace(
            "`seed.txt`", "`src/Missing.cs`", StringComparison.Ordinal);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, plan, string.Empty, clock);
        Xunit.Assert.True(File.Exists(process.ExitCodePath));
        Xunit.Assert.Contains("`src/Missing.cs`", File.ReadAllText(process.StandardOutputPath), StringComparison.Ordinal);

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var failure = Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.StartsWith(
            "Dispatch failed: rule=planner-output-contract-rejected: Planner output contract failed.",
            failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Offending plan line ", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.EndsWith(" Command: " + task.LastDispatch!.Command, failure.Message, StringComparison.Ordinal);
    }
}
