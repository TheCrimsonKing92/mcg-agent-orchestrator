using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelSuspensionTests
{
    [Theory]
    [InlineData("EmptyOutput")]
    [InlineData("InvalidOutput")]
    [InlineData("InvocationFailed")]
    public async Task Two_protocol_failures_suspend_only_that_judge(string fault)
    {
        using var h = new PanelTestHarness();
        h.Sol.Fault = Enum.Parse<PanelJudgeOutcome>(fault);
        h.Enqueue("one");
        await h.Complete();
        Assert.Equal(PanelJudgeHealthState.Active, h.Store.Health("sol").State);
        var second = h.Enqueue("two");
        await h.Complete();
        var health = h.Store.Health("sol");
        Assert.Equal(PanelJudgeHealthState.Suspended, health.State);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.Equal(second.Id, health.SuspendedCaseId);
        var third = h.Enqueue("three");
        await h.Complete();
        var skipped = h.Store.Results(third.Id).Single(result => result.Judge == "sol");
        Assert.Equal(PanelJudgeOutcome.Skipped, skipped.Outcome);
        Assert.Equal("judge-suspended", skipped.Reason);
        Assert.Equal(2, h.Sol.Calls);
        Assert.Equal(3, h.Sonnet.Calls);
        Assert.Equal(PanelJudgeOutcome.Valid, h.Store.Results(third.Id).Single(result => result.Judge == "sonnet").Outcome);
        Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(third.Id)!.Terminal);
    }

    [Fact]
    public async Task Valid_output_resets_failures_and_timeouts_do_not_suspend()
    {
        using var h = new PanelTestHarness();
        h.Sol.Fault = PanelJudgeOutcome.EmptyOutput;
        h.Enqueue("one"); await h.Complete();
        h.Sol.Fault = null;
        h.Enqueue("two"); await h.Complete();
        Assert.Equal(0, h.Store.Health("sol").ConsecutiveFailures);
        h.Sol.Fault = PanelJudgeOutcome.TimedOut;
        h.Enqueue("three"); await h.Complete();
        h.Enqueue("four"); await h.Complete();
        Assert.Equal(PanelJudgeHealthState.Active, h.Store.Health("sol").State);
    }

    [Fact]
    public async Task Both_suspended_judges_produce_suspended_skip_without_calls()
    {
        using var h = new PanelTestHarness();
        h.Sol.Fault = h.Sonnet.Fault = PanelJudgeOutcome.EmptyOutput;
        h.Enqueue("one"); await h.Complete();
        h.Enqueue("two"); await h.Complete();
        var third = h.Enqueue("three"); await h.Complete();
        Assert.Equal(PanelCaseTerminal.SuspendedSkip, h.Store.Get(third.Id)!.Terminal);
        Assert.All(h.Store.Results(third.Id), result => Assert.Equal("judge-suspended", result.Reason));
        Assert.Equal(2, h.Sol.Calls);
        Assert.Equal(2, h.Sonnet.Calls);
    }
}
