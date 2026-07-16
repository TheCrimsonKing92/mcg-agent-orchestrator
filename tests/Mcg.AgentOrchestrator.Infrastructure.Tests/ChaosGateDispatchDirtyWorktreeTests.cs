using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDispatchDirtyWorktreeTests : ChaosGateTestBase
{
    // Gate 4: Dirty worktree at completion
    [Xunit.Fact(DisplayName = "ChaosGate4_dirty_worktree_at_completion_is_rejected")]
    public void Gate4_DirtyWorktreeAtCompletion_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt => File.WriteAllText(Path.Combine(wt, "Dirty.cs"), "// uncommitted"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    }
}
