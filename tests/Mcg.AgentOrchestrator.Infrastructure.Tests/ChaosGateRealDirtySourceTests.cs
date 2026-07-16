using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateRealDirtySourceTests : ChaosGateTestBase
{
    // Regression: real uncommitted source still trips dirty guard
    [Xunit.Fact(DisplayName = "Leniency_RealUncommittedSourceFile_still_trips_dirty_guard")]
    public void Leniency_RealUncommittedSourceFile_StillTripsDirtyGuard()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt =>
            {
                // Uncommitted real source file - should still trip the dirty guard.
                File.WriteAllText(Path.Combine(wt, "RealDirty.cs"), "// uncommitted source");
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    }
}
