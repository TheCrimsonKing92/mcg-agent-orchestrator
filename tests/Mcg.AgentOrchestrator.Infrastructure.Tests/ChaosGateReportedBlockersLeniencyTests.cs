using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateReportedBlockersLeniencyTests : ChaosGateTestBase
{
    // Leniency: reported blockers are advisory; committed work passes
    [Xunit.Fact(DisplayName = "Leniency_ReportedBlockers_passes_advisory_when_work_committed")]
    public void Leniency_ReportedBlockers_PassesAdvisoryWhenWorkCommitted()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "Passed", blockers: "API rate limit hit; retry after 1h"),
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // The blockers field is advisory and no longer gates the dispatch. The relevant committed
        // change on a clean worktree is the substance; the blocker note remains in the recorded
        // verification output for the operator/scorecard.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
