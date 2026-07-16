using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateNoWorkerResultTests : ChaosGateTestBase
{
    // Regression: no WORKER_RESULT anywhere (no stdout block, no file)
    [Xunit.Fact(DisplayName = "Leniency_NoWorkerResultAndNoChange_still_fails_on_git_gate")]
    public void Leniency_NoWorkerResultAndNoChange_StillFailsOnGitGate()
    {
        // Advisory WORKER_RESULT cannot be exploited by omitting the block: with no block AND no
        // relevant committed change, the git no-change gate (not the contract) fails the dispatch.
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            "Done. Files written.",
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
