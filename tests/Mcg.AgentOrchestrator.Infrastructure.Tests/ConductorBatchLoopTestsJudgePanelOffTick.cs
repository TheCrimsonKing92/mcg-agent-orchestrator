using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsJudgePanelOffTick(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void Loop_tick_returns_while_judge_is_blocked_and_harvest_precedes_next_launch()
    {
        using var h = new PanelTestHarness();
        h.Sol.Hold();
        var first = h.Enqueue("one");
        var second = h.Enqueue("two");
        var observed = 0;
        var firstHarvestedBeforeSecondCall = false;
        h.Sol.Reply = item =>
        {
            if (item.Id == second.Id) firstHarvestedBeforeSecondCall = h.Store.Get(first.Id)!.Terminal == PanelCaseTerminal.Completed;
            return PanelTestHarness.Answer(item.Id);
        };
        new ConductorBatchLoop(utcNow: () => h.Time.UtcNow).WithJudgePanel(h.Host).Run(h.Kernel, MakeDriver(utcNow: () => h.Time.UtcNow),
            ConductorAutonomyPolicy.Conservative, Path.Combine(h.Root, "stop.signal"), maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false,
            keepAliveWhenIdle: true, persistGoalTick: (_, _) => { }, onTick: _ =>
            {
                observed++;
                if (observed == 1)
                {
                    PanelTestHarness.Signal(h.Sol.Started.Task, "first judge started").GetAwaiter().GetResult();
                    Assert.False(h.Host.CurrentCase!.IsCompleted);
                    Assert.Equal("in-flight", h.Store.Get(first.Id)!.Status);
                    Assert.Equal("pending", h.Store.Get(second.Id)!.Status);
                    Assert.Equal(1, h.Sol.Calls);
                    h.Sol.Release();
                    PanelTestHarness.Signal(h.Host.CurrentCase!, "first case finished").GetAwaiter().GetResult();
                    Assert.Equal(1, h.Sol.Calls);
                    Assert.Equal("pending", h.Store.Get(second.Id)!.Status);
                }
                else
                {
                    PanelTestHarness.Signal(h.Host.CurrentCase!, "second case finished").GetAwaiter().GetResult();
                    Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(first.Id)!.Terminal);
                    Assert.Equal("in-flight", h.Store.Get(second.Id)!.Status);
                    Assert.Equal(2, h.Sol.Calls);
                    Assert.True(firstHarvestedBeforeSecondCall);
                }
            });
        Assert.Equal(2, observed);
    }
}
