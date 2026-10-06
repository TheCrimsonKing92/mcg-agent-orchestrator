using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsJudgePanelIncrementalLedger(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public async Task IdleJudgePanelStepReportsZeroCountedReads()
    {
        var cpu = TimeSpan.Zero;
        var reads = new PanelSourceReadCounter { OnEventLine = () => cpu += TimeSpan.FromMilliseconds(1) };
        using var h = new PanelIncrementalTestFixture(reads);
        await h.Fixture.SaveCriteria();
        h.Escalate(1);
        var phases = new List<string>();
        var loop = new ConductorBatchLoop(utcNow: () => h.Fixture.Panel.Time.UtcNow, processCpuTime: () => cpu).WithJudgePanel(h.Host);
        loop.Run(h.Fixture.Panel.Kernel, MakeDriver(utcNow: () => h.Fixture.Panel.Time.UtcNow),
            ConductorAutonomyPolicy.Conservative, Path.Combine(h.Fixture.Panel.Root, "stop.signal"), maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false, keepAliveWhenIdle: true,
            persistGoalTick: (_, _) => { }, onTick: tick =>
            {
                phases.Add(ConductorBatchLoopTestsStepLedger.Phase(tick, "prewalk"));
                Assert.Single(h.Fixture.Panel.Store.Cases());
                Assert.Equal(phases.Count == 1 ? 1 : 0, reads.EventLines);
                Assert.Equal(phases.Count == 1 ? 1 : 0, reads.ParsedLines);
                Assert.Equal(phases.Count == 1 ? 1 : 0, reads.GoalReads);
                reads.Reset();
            });
        Assert.Equal(2, phases.Count);
        Assert.Contains("judge-panel:1:1", phases[0]);
        Assert.Contains("judge-panel:0:1", phases[1]);
    }
}
