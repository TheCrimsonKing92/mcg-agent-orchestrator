using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBatchLoopTestsActivationHeartbeat(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void SupervisedIdleCyclesEmitCompletedTickHeartbeats()
    {
        var sleeps = 0;
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 5, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => ++sleeps == 3, keepAliveWhenIdle: true,
                emitActivationHeartbeat: true);
        });

        Assert.Equal(3, sleeps);
        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Count(line => line.StartsWith("TICK_END tick=1 activation=true ",
            StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void SupervisedQuietWorkerTicksEmitCompletedTickHeartbeats()
    {
        var (kernel, _) = SimpleGoal();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 4, watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ => false, emitActivationHeartbeat: true);
        });

        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        Assert.Equal(4, lines.Count(line => line.StartsWith("TICK_END tick=", StringComparison.Ordinal) &&
            line.Contains(" activation=true ", StringComparison.Ordinal)));
    }
}
