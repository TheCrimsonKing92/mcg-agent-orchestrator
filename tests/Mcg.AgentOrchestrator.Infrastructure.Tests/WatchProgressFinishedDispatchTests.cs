using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WatchProgressFinishedDispatchTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    public void AssignedTaskWithVerificationAtOrAfterDispatchIsNotWatched(int verificationMinutes, int exitCode)
    {
        var (kernel, goal) = GoalWithAssignedDispatchAndVerification(verificationMinutes, exitCode);
        var current = kernel.GetGoal(goal.Id);
        Assert.Null(ConductorWatchProgressReporter.GetActiveTask(current));
        Assert.DoesNotContain(
            NewReporter().BuildLines(current, quiet: false, policy: ConductorAutonomyPolicy.Conservative),
            line => line.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void AssignedTaskWithoutLaterVerificationRemainsWatched(int? verificationMinutes)
    {
        var (kernel, goal) = GoalWithAssignedDispatchAndVerification(verificationMinutes, exitCode: 1);
        var current = kernel.GetGoal(goal.Id);
        Assert.Equal(current.Tasks.Single().Id, ConductorWatchProgressReporter.GetActiveTask(current)?.Id);
        Assert.Single(
            NewReporter().BuildLines(current, quiet: false, policy: ConductorAutonomyPolicy.Conservative)
                .Where(line => line.StartsWith("WATCH_PROGRESS ", StringComparison.Ordinal) &&
                    line.Contains("role=Developer", StringComparison.Ordinal)));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) GoalWithAssignedDispatchAndVerification(
        int? verificationMinutes,
        int exitCode)
    {
        var (kernel, goal) = SimpleGoal("watch a dispatched Developer");
        var task = goal.Tasks.Single();
        var dispatchedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "run", "C:\\repo", dispatchedAt));
        if (verificationMinutes is { } offset)
        {
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify", "C:\\repo", exitCode, exitCode == 0 ? "ok" : "", exitCode == 0 ? "" : "failed", dispatchedAt.AddMinutes(offset)));
        }

        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(savedGoal => savedGoal.Id == goal.Id.Value
                ? savedGoal with
                {
                    Tasks = savedGoal.Tasks.Select(savedTask => savedTask.Id == task.Id.Value
                        ? savedTask with { Status = WorkTaskStatus.Assigned }
                        : savedTask).ToArray()
                }
                : savedGoal).ToArray()
        });
        return (kernel, kernel.GetGoal(goal.Id));
    }

    private static ConductorWatchProgressReporter NewReporter() => new(
        readChanges: (_, _) => new DispatchLiveChangeSnapshot([], [], 0),
        isProcessAlive: _ => false,
        now: () => new DateTimeOffset(2026, 9, 25, 12, 10, 0, TimeSpan.Zero),
        loopProcessId: 9001,
        launcherProcessId: 9002);
}
