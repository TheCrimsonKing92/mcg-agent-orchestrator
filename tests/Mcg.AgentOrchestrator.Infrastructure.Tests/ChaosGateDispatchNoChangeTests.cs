using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDispatchNoChangeTests : ChaosGateTestBase
{
    // Gate 1: False-positive completion rejection
    [Xunit.Fact(DisplayName = "ChaosGate1_worker_exits_0_with_no_file_change_is_rejected")]
    public void Gate1_WorkerExitsZeroWithNoFileChange_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: null);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(
            "did not produce required relevant file-change evidence",
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }
}
