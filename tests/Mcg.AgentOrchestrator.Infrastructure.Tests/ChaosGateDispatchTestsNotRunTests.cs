using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDispatchTestsNotRunTests : ChaosGateTestBase
{
    // Gate 6: tests not run at dispatch -> advisory pass (acceptance enforces tests)
    [Xunit.Fact(DisplayName = "ChaosGate6_tests_not_run_at_dispatch_passes_advisory_acceptance_enforces_tests")]
    public void Gate6_TestsNotRunAtDispatch_PassesAdvisory_AcceptanceEnforcesTests()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Mcg.AgentOrchestrator.Core/Application/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// core feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Test evidence is no longer taken from the worker's self-report at dispatch time; the
        // actual test run is enforced by the acceptance gate (GoalAcceptanceVerifier). A relevant
        // committed change on a clean worktree is sufficient to complete the dispatch.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
