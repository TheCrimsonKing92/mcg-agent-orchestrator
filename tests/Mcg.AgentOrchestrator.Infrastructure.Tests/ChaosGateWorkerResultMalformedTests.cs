using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateWorkerResultMalformedTests : ChaosGateTestBase
{
    // Gate 3b: Malformed WORKER_RESULT (missing skills field)
    [Xunit.Fact(DisplayName = "ChaosGate3b_malformed_worker_result_missing_field_passes_advisory")]
    public void Gate3b_MalformedWorkerResultMissingField_PassesAdvisory()
    {
        var root = CreateSeededRepo();
        const string malformed =
            """
            WORKER_RESULT:
            files: src/Feature.cs
            commands: dotnet build
            tests: Passed
            commit: none
            blockers: none
            model_fit: OpenAI/gpt-5.5 - adequate - chaos test fixture
            END_WORKER_RESULT
            """;
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer, malformed, string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, "src/Feature.cs", "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Field shape is advisory: an odd/partial block (here missing skills) no longer fails the
        // dispatch when git shows a relevant committed change on a clean worktree.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
