using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDeferredVerificationBlockerTests : ChaosGateTestBase
{
    [Xunit.Fact(DisplayName = "Regression_89a2c42_deferred_verification_blocker_is_advisory_for_dirty_changed_work")]
    public void Regression89a2c42DeferredVerificationBlockerIsAdvisoryForDirtyChangedWork()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(
                relPath,
                "dotnet test --filter WorkerDispatch",
                "pass - focused dispatch-runner coverage",
                blockers: "full suite deferred to orchestrator acceptance gate per current-task.md"),
            string.Empty,
            mutateWorktree: wt =>
            {
                var fullPath = Path.Combine(wt, relPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, "// feature");
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.True(task.LastVerification.HasCommittedChanges);
        Assert.Contains("full suite deferred", task.LastVerification.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskCompleted &&
            evt.Message.Contains("advisory WORKER_RESULT blocker", StringComparison.Ordinal));
    }
}
