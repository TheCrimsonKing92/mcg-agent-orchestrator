using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateWorkerResultMissingTests : ChaosGateTestBase
{
    // Gate 3a: Missing WORKER_RESULT -> advisory pass (git evidence carries substance)
    [Xunit.Fact(DisplayName = "ChaosGate3a_missing_worker_result_block_passes_advisory_when_git_evidence_present")]
    public void Gate3a_MissingWorkerResultBlock_PassesAdvisoryWhenGitEvidencePresent()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            "Done. Files written.",
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, "src/Feature.cs", "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // WORKER_RESULT is advisory. A relevant committed change on a clean worktree carries the
        // substance, so a missing block no longer fails the dispatch. The git gates (Gate1/4/5)
        // still catch no-change / dirty / noise-only worktrees regardless of the block.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
