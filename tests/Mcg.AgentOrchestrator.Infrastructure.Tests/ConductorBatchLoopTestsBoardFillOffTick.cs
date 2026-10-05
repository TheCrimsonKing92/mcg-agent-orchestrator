using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsBoardFillOffTick(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void Loop_services_later_ticks_while_drafting_is_held()
    {
        using var h = new BoardFillTestHarness();
        h.Held = true;
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        var ticks = 0;
        new ConductorBatchLoop(utcNow: () => h.Clock.UtcNow).WithBoardFill(h.Host).Run(h.Kernel,
            MakeDriver(utcNow: () => h.Clock.UtcNow), ConductorAutonomyPolicy.Conservative,
            Path.Combine(h.Root, "stop.signal"), maxIterations: 2, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false, keepAliveWhenIdle: true, persistGoalTick: (_, _) => { }, onTick: _ =>
            {
                ticks++;
                if (ticks == 1)
                {
                    PanelTestHarness.Signal(h.Started.Task, "off-tick draft started").GetAwaiter().GetResult();
                    Assert.False(h.Host.CurrentRound!.IsCompleted);
                    Assert.Null(Assert.Single(h.Store.ReadAll()).Outcome);
                    Assert.Empty(h.Events());
                    h.Release();
                    PanelTestHarness.Signal(h.Host.CurrentRound, "off-tick draft finished").GetAwaiter().GetResult();
                    Assert.Null(Assert.Single(h.Store.ReadAll()).Outcome);
                }
                else
                {
                    Assert.Equal("draft", Assert.Single(h.Store.ReadAll()).Outcome);
                    Assert.Single(h.Events());
                    Assert.Equal(1, h.Calls);
                }
            });
        Assert.Equal(2, ticks);
        Assert.Single(h.Events());
    }

    [Fact]
    public void Drafting_exception_does_not_stop_the_conductor_loop()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        h.Reply = () => throw new InvalidOperationException("injected loop failure");
        var ticks = 0;
        new ConductorBatchLoop(utcNow: () => h.Clock.UtcNow).WithBoardFill(h.Host).Run(h.Kernel,
            MakeDriver(utcNow: () => h.Clock.UtcNow), ConductorAutonomyPolicy.Conservative,
            Path.Combine(h.Root, "stop.signal"), maxIterations: 2, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false, keepAliveWhenIdle: true, persistGoalTick: (_, _) => { }, onTick: _ =>
            {
                ticks++;
                if (ticks == 1)
                    Assert.Throws<InvalidOperationException>(() =>
                        PanelTestHarness.Signal(h.Host.CurrentRound!, "failed off-tick round finished").GetAwaiter().GetResult());
                else
                {
                    Assert.Equal("failed", Assert.Single(h.Store.ReadAll()).Outcome);
                    Assert.Contains("injected loop failure", Assert.Single(h.Store.ReadAll()).Failure);
                    Assert.Single(h.Events());
                }
            });
        Assert.Equal(2, ticks);
        Assert.Equal(1, h.Calls);
        Assert.Single(h.Events());
    }
}
