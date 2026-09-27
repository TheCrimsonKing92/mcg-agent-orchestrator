using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopTestsIdleActivationHeartbeat(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void IdleLoopWritesOneActivationHeartbeatBeforeEachIdleSleep()
    {
        AssertIdleHeartbeatOrder(blockedRecheck: false);
    }

    [Xunit.Fact]
    public void IdleLoopWritesActivationHeartbeatBeforeBlockedRecheckSleep()
    {
        AssertIdleHeartbeatOrder(blockedRecheck: true);
    }

    private static void AssertIdleHeartbeatOrder(bool blockedRecheck)
    {
        var sleeps = 0;
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
        {
            new ConductorBatchLoop().Run(
                new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 10, watchInterval: TimeSpan.FromSeconds(120),
                sleepFunc: _ =>
                {
                    Console.WriteLine("FAKE_SLEEP_ENTERED");
                    return ++sleeps == 3;
                },
                keepAliveWhenIdle: true, emitActivationHeartbeat: true,
                hasTransientLoadHold: blockedRecheck ? () => true : null);
        });

        Assert.Equal(3, sleeps);
        var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        var progressPrefix = blockedRecheck ? "BLOCKED_RECHECK_SLEEP " : "IDLE_SLEEP ";
        var heartbeatPrefix = "TICK_END tick=1 activation=true ";
        var start = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var sleep = Array.FindIndex(lines, start, line => line == "FAKE_SLEEP_ENTERED");
            Assert.True(sleep > start, $"Idle iteration {iteration + 1} did not enter the sleep seam.");
            var segment = lines[start..sleep];
            var heartbeat = Array.FindIndex(segment, line =>
                line.StartsWith(heartbeatPrefix, StringComparison.Ordinal));
            var progress = Array.FindIndex(segment, line =>
                line.StartsWith(progressPrefix, StringComparison.Ordinal));
            Assert.True(heartbeat >= 0 && heartbeat < progress,
                $"Idle iteration {iteration + 1} must write its activation heartbeat before {progressPrefix.Trim()}.");
            Assert.Single(segment.Where(line =>
                line.StartsWith(heartbeatPrefix, StringComparison.Ordinal)));
            start = sleep + 1;
        }
        Assert.Equal(3, lines.Count(line =>
            line.StartsWith(heartbeatPrefix, StringComparison.Ordinal)));
    }
}
