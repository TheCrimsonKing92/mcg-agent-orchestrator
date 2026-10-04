using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorJudgePanelTriggerFailureTests
{
    [Fact]
    public async Task Poisoned_packet_keeps_later_enrollment_harvest_reporting_and_claims_live()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        f.Panel.Sol.Hold();
        f.Panel.Sonnet.Hold();
        var host = new ConductorJudgePanelHost(f.Panel.Store, f.Panel.Sol, f.Panel.Sonnet,
            _ => f.Panel.Candidate, new ConductEventLogWriter(f.Panel.ConductPath), () => f.Panel.Time.UtcNow)
            { Triggers = f.Detector() };
        try
        {
            var running = f.Panel.Enqueue("already-running");
            host.ServiceTick(f.Panel.Kernel);
            await PanelTestHarness.Signal(f.Panel.Sol.Started.Task, "first sol judge started");
            await PanelTestHarness.Signal(f.Panel.Sonnet.Started.Task, "first sonnet judge started");

            var poison = f.Panel.Kernel.CreateGoal("Oversized protected criteria",
                [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
            f.Panel.Kernel.RecordGoalRefinement(poison.Id, new("Poison",
                [new string('c', ConductorJudgePanelPacketBuilder.MaxPacketCharacters)],
                VerificationClass.TestVerifiable, [], []));
            await f.SaveState();
            f.Timeline(0, "PRE_TESTER_RED_LOOP: poison", goalId: poison.Id.Value);
            f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
            f.Timeline(1, f.BoundText("PRE_REVIEW_EVIDENCE_TIMEOUT: later valid dispute", f.Trx()));

            // Detect the bad record before the valid one while a judge round is still in flight.
            host.ServiceTick(f.Panel.Kernel);
            var pending = Assert.Single(f.Panel.Store.Cases().Where(item => item.Status == "pending"));
            Assert.Equal($"goal-event:{f.GoalId}:1", pending.Key.TriggerId);
            Assert.Equal("in-flight", f.Panel.Store.Get(running.Id)!.Status);
            var failure = Assert.Single(f.Panel.Store.TriggerFailures());
            Assert.Equal($"goal-event:{poison.Id.Value}:0", failure.TriggerId);
            Assert.Equal(poison.Id.Value, failure.GoalId);
            Assert.Contains("InvalidDataException: Panel protected criteria and authority sections exceed MaxPacketCharacters", failure.Error);

            f.Panel.Sol.Release();
            f.Panel.Sonnet.Release();
            await PanelTestHarness.Signal(host.CurrentCase!, "first panel round completed despite poison");
            host.ServiceTick(f.Panel.Kernel);
            Assert.Equal(PanelCaseTerminal.Completed, f.Panel.Store.Get(running.Id)!.Terminal);
            Assert.Contains("PANEL_CASE case=" + running.Id, File.ReadAllText(f.Panel.ConductPath));
            Assert.Equal("in-flight", f.Panel.Store.Get(pending.Id)!.Status);
            await PanelTestHarness.Signal(host.CurrentCase!, "later valid panel round completed despite poison");
            host.ServiceTick(f.Panel.Kernel);
            Assert.Equal(PanelCaseTerminal.Completed, f.Panel.Store.Get(pending.Id)!.Terminal);
            Assert.Contains("PANEL_CASE case=" + pending.Id, File.ReadAllText(f.Panel.ConductPath));
            Assert.Null(host.CurrentCase);
            Assert.Equal(2, f.Panel.Sol.Calls);
            Assert.Equal(2, f.Panel.Sonnet.Calls);
            var reopened = new ConductorJudgePanelCaseStore(Path.Combine(f.Panel.Root, "panel.db"));
            Assert.Equal(failure, Assert.Single(reopened.TriggerFailures()));
            Assert.Equal(2, reopened.Cases().Count);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Ambiguous_conduct_prefix_records_failure_and_does_not_block_later_trigger()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var other = f.Sources.ReadGoal(f.GoalId)! with { Id = f.GoalId[..8] + new string('d', 24) };
        await new SqliteOrchestratorStateRepository(f.State).SaveGoalSnapshotsAsync([other]);
        var prefix = f.GoalId[..8];
        var reason = $"PRE_REVIEW_EVIDENCE_TIMEOUT: candidate {f.Panel.Candidate} hit its budget";
        var at = f.Panel.Time.UtcNow;
        f.Conduct("goal", $"GOAL goal={prefix} result=escalated reason={ConductorBatchLoop.SanitizeReason(reason)}", goalId: prefix);
        f.Panel.Time.UtcNow += TimeSpan.FromSeconds(1);
        f.Timeline(0, "PRE_TESTER_RED_LOOP: later valid dispute");
        f.PrepareProducerReadArtifacts();
        var before = f.ProducerHashes();

        var item = Assert.Single(f.Detector().Detect());
        Assert.Equal($"goal-event:{f.GoalId}:0", item.Key.TriggerId);
        var failure = Assert.Single(f.Panel.Store.TriggerFailures());
        Assert.Equal($"conduct-event:{prefix}:{at:O}", failure.TriggerId);
        Assert.Equal(prefix, failure.GoalId);
        Assert.Equal("InvalidDataException: Ambiguous panel trigger goal prefix: " + prefix, failure.Error);
        var reopened = new ConductorJudgePanelCaseStore(Path.Combine(f.Panel.Root, "panel.db"));
        Assert.Empty(f.Detector(reopened).Detect());
        Assert.Single(reopened.TriggerFailures());
        Assert.Single(reopened.Cases());
        Assert.Equal(before.OrderBy(pair => pair.Key), f.ProducerHashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Source_scan_failure_keeps_harvest_reporting_and_next_claim_live()
    {
        using var f = new PanelTriggerTestFixture();
        await f.SaveCriteria();
        var host = new ConductorJudgePanelHost(f.Panel.Store, f.Panel.Sol, f.Panel.Sonnet,
            _ => f.Panel.Candidate, new ConductEventLogWriter(f.Panel.ConductPath), () => f.Panel.Time.UtcNow)
            { Triggers = f.Detector() };
        try
        {
            var first = f.Panel.Enqueue("first");
            var next = f.Panel.Enqueue("next");
            host.ServiceTick(f.Panel.Kernel);
            await PanelTestHarness.Signal(host.CurrentCase!, "first round completed before scan failure");
            File.WriteAllText(f.Author, "not a SQLite database");
            host.ServiceTick(f.Panel.Kernel);
            Assert.Equal(PanelCaseTerminal.Completed, f.Panel.Store.Get(first.Id)!.Terminal);
            Assert.Contains("PANEL_CASE case=" + first.Id, File.ReadAllText(f.Panel.ConductPath));
            Assert.Equal("in-flight", f.Panel.Store.Get(next.Id)!.Status);
            var failure = Assert.Single(f.Panel.Store.TriggerFailures());
            Assert.Equal("source-scan", failure.TriggerId);
            Assert.Contains("SqliteException:", failure.Error);
            await PanelTestHarness.Signal(host.CurrentCase!, "second round completed despite scan failure");
            host.ServiceTick(f.Panel.Kernel);
            Assert.Equal(PanelCaseTerminal.Completed, f.Panel.Store.Get(next.Id)!.Terminal);
            Assert.Null(host.CurrentCase);
            Assert.Equal(2, f.Panel.Sol.Calls);
            Assert.Equal(2, f.Panel.Sonnet.Calls);
        }
        finally { host.Stop(); }
    }
}
