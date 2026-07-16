using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateFabricatedCommitLeniencyTests : ChaosGateTestBase
{
    // Leniency: fabricated commit SHA passes when a real relevant commit exists
    [Xunit.Fact(DisplayName = "Leniency_FabricatedCommitSha_passes_advisory_when_real_commit_present")]
    public void Leniency_FabricatedCommitSha_PassesAdvisoryWhenRealCommitPresent()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";

        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "Passed", commit: "deadbeef"),
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // The worker reported a fabricated commit ("deadbeef"), but git shows a real relevant
        // commit on a clean worktree. The self-reported commit is advisory and no longer
        // cross-checked, so git ground truth completes the dispatch.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
