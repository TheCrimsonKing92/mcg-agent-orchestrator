using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorJudgePanelShadowTests
{
    [Fact]
    public async Task Completion_changes_only_panel_store_and_one_outcome_event()
    {
        using var h = new PanelTestHarness();
        var timeline = h.Goal.Timeline.ToArray();
        var hold = h.Goal.CurrentHold;
        var status = h.Goal.Status;
        var tasks = h.Goal.Tasks.Select(task => (task.Id, task.Status)).ToArray();
        var item = h.Enqueue("shadow");
        await h.Complete();
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(item.Id)!.Terminal);
        Assert.Equal(timeline, h.Goal.Timeline);
        Assert.Equal(hold, h.Goal.CurrentHold);
        Assert.Equal(status, h.Goal.Status);
        Assert.Equal(tasks, h.Goal.Tasks.Select(task => (task.Id, task.Status)));
        var line = Assert.Single(File.ReadAllLines(h.ConductPath));
        using var document = JsonDocument.Parse(line);
        Assert.Equal("judge-panel", document.RootElement.GetProperty("eventKind").GetString());
        Assert.Contains("PANEL_CASE", document.RootElement.GetProperty("detail").GetString());
        Assert.Equal("outcome", document.RootElement.GetProperty("operator").GetString());
    }

    [Fact]
    public void Missing_bindings_keep_panel_idle_with_durable_reason()
    {
        using var h = new PanelTestHarness();
        var item = h.Enqueue("idle");
        var host = new ConductorJudgePanelHost(h.Store, new CodexSolPanelJudgeRunner(Mcg.AgentOrchestrator.Core.ModelFunctionCatalog.Empty),
            new ClaudeSonnetPanelJudgeRunner(Mcg.AgentOrchestrator.Core.ModelFunctionCatalog.Empty), _ => h.Candidate,
            new ConductEventLogWriter(h.ConductPath), () => h.Time.UtcNow);
        try { host.ServiceTick(h.Kernel); }
        finally { host.Stop(); }
        Assert.Equal("pending", h.Store.Get(item.Id)!.Status);
        Assert.Empty(h.Store.Results(item.Id));
        Assert.Null(host.CurrentCase);
        Assert.Contains("panel-binding-invalid:", h.Store.IdleReason());
        Assert.False(File.Exists(h.ConductPath));
    }
}
