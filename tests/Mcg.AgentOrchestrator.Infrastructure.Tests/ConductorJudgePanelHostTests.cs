using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorJudgePanelHostTests
{
    [Fact]
    public async Task Records_both_valid_results_and_duplicate_key_is_no_op()
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("first");
        await h.Complete();
        Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(item.Id)!.Terminal);
        var results = h.Store.Results(item.Id);
        Assert.Equal(new[] { "sol", "sonnet" }, results.Select(result => result.Judge));
        Assert.All(results, result => Assert.Equal(PanelJudgeOutcome.Valid, result.Outcome));
        Assert.Equal(h.Store.Get(item.Id), h.Store.Enqueue(item.Key));
        Assert.Single(h.Store.Cases());
        Assert.Equal(1, h.Sol.Calls);
        Assert.Equal(1, h.Sonnet.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_candidate_or_criteria_is_superseded_with_both_receipts(bool criteria)
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("first");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentCase!, "judges finished before supersession");
        if (criteria)
            h.Kernel.SetGoalRefinedSpec(h.Goal.Id, new RefinedSpec("new spec", ["new criterion"], VerificationClass.TestVerifiable, [], []));
        else h.Candidate = new('c', 40);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(PanelCaseTerminal.Superseded, h.Store.Get(item.Id)!.Terminal);
        Assert.Equal(2, h.Store.Results(item.Id).Count);
        Assert.All(h.Store.Results(item.Id), result => Assert.Equal(PanelJudgeOutcome.Valid, result.Outcome));
        Assert.Contains("result=superseded", File.ReadAllText(h.ConductPath));
    }

    [Fact]
    public async Task Completed_shutdown_handoff_is_harvested_without_relaunch()
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("first");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentCase!, "judges finished before shutdown");
        h.Host.Stop();
        Assert.Equal("ready", h.Store.Get(item.Id)!.Status);
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(h.Root, "panel.db"));
        Assert.Equal(2, reopened.Results(item.Id).Count);
        var next = h.NewHost();
        try { await h.Complete(next); }
        finally { next.Stop(); }
        Assert.Equal(PanelCaseTerminal.Completed, reopened.Get(item.Id)!.Terminal);
        Assert.Equal(1, h.Sol.Calls);
        Assert.Equal(1, h.Sonnet.Calls);
        Assert.Single(File.ReadAllLines(h.ConductPath));
    }

    [Fact]
    public async Task Interrupted_shutdown_keeps_reservations_and_does_not_retry()
    {
        using var h = new PanelTestHarness();
        h.Sol.Hold();
        var item = h.Enqueue("interrupted");
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Sol.Started.Task, "Sol started before interruption");
        Assert.False(h.Host.CurrentCase!.IsCompleted);
        h.Host.Stop();
        Assert.Equal("ready", h.Store.Get(item.Id)!.Status);
        Assert.Equal(PanelJudgeOutcome.TimedOut, h.Store.Results(item.Id).Single(result => result.Judge == "sol").Outcome);
        var next = h.NewHost();
        try { await h.Complete(next); }
        finally { next.Stop(); }
        Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(item.Id)!.Terminal);
        Assert.Equal(1, h.Sol.Calls);
        Assert.Equal(1, h.Sonnet.Calls);
    }

    [Fact]
    public async Task Expired_claim_preserves_partial_receipt_and_never_retries_reserved_calls()
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("first");
        var claim = h.Store.ClaimNext(h.Time.UtcNow)!;
        Assert.True(h.Store.ReserveCall(claim, "sol"));
        Assert.True(h.Store.ReserveCall(claim, "sonnet"));
        h.Store.RecordResult(claim, new("sol", PanelJudgeOutcome.Valid, 0, PanelTestHarness.Answer(item.Id), ""));
        h.Time.UtcNow += ConductorJudgePanelBudgets.ClaimExpiry + TimeSpan.FromSeconds(1);
        await h.Complete();
        Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(item.Id)!.Terminal);
        Assert.Equal(PanelJudgeOutcome.Valid, h.Store.Results(item.Id).Single(result => result.Judge == "sol").Outcome);
        var missing = h.Store.Results(item.Id).Single(result => result.Judge == "sonnet");
        Assert.Equal(PanelJudgeOutcome.TimedOut, missing.Outcome);
        Assert.Equal("claim-expired", missing.Reason);
        Assert.Equal(0, h.Sol.Calls);
        Assert.Equal(0, h.Sonnet.Calls);
        h.Store.RecordResult(claim, new("sonnet", PanelJudgeOutcome.Valid, 0, "late", ""));
        Assert.Equal(missing, h.Store.Results(item.Id).Single(result => result.Judge == "sonnet"));
    }
}
