using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateUntrackedWorkerResultFileTests : ChaosGateTestBase
{
    // Leniency: untracked WORKER_RESULT.md does not trip dirty guard
    [Xunit.Fact(DisplayName = "Leniency_UntrackedWorkerResultFile_does_not_trip_dirty_worktree_guard")]
    public void Leniency_UntrackedWorkerResultFile_DoesNotTripDirtyWorktreeGuard()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "Passed"),
            string.Empty,
            mutateWorktree: wt =>
            {
                CommitSourceFile(wt, relPath, "// feature");
                // Leave an UNTRACKED WORKER_RESULT.md - should be invisible to dirty guard.
                var commit = ReadGit(wt, ["rev-parse", "--short", "HEAD"]);
                File.WriteAllText(Path.Combine(wt, "WORKER_RESULT.md"),
                    WorkerResultBlock(relPath, "dotnet build", "Passed", commit: commit));
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Untracked WORKER_RESULT.md must not trigger the dirty-worktree gate.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.False(task.LastVerification!.StandardError.Contains(
            "left the worktree dirty", StringComparison.Ordinal));
    }
}
