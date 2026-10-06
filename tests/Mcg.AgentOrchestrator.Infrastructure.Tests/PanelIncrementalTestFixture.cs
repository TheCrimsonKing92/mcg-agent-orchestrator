using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.Data.Sqlite;

// Every producer and ledger is isolated. Binding errors hold the host idle after
// real trigger discovery, so judge completion cannot append unrelated conduct lines.
internal sealed class PanelIncrementalTestFixture : IDisposable
{
    internal PanelTriggerTestFixture Fixture { get; } = new();
    internal PanelSourceReadCounter Reads { get; }
    internal ConductorJudgePanelHost Host { get; private set; }
    internal string TimelinePath => Path.Combine(Fixture.Events, Fixture.GoalId + ".jsonl");
    internal PanelIncrementalTestFixture(PanelSourceReadCounter? reads = null)
    {
        Reads = reads ?? new();
        Host = NewHost(Fixture.Panel.Store);
    }
    private ConductorJudgePanelHost NewHost(ConductorJudgePanelCaseStore store)
    {
        var f = Fixture;
        var sources = new ConductorJudgePanelTriggerSources(f.Events, f.Panel.ConductPath, f.Author, f.Cohort, f.State) { Reads = Reads };
        return new(store, new PanelFakeJudge("sol") { BindingError = "fixture holds discovery" },
            new PanelFakeJudge("sonnet") { BindingError = "fixture holds discovery" }, _ => f.Panel.Candidate,
            new ConductEventLogWriter(f.Panel.ConductPath, utcNow: () => f.Panel.Time.UtcNow), () => f.Panel.Time.UtcNow)
        { Triggers = new(store, sources, f.Packets) };
    }
    internal void Relaunch()
    {
        Host.Stop();
        Host = NewHost(new ConductorJudgePanelCaseStore(Path.Combine(Fixture.Panel.Root, "panel.db")));
    }
    internal void Tick(string? onlyGoalId = null) => Host.ServiceTick(Fixture.Panel.Kernel, onlyGoalId);
    internal void Escalate(int cursor, string code = "PRE_REVIEW_RED_UNCHANGED_CANDIDATE") =>
        Fixture.Timeline(cursor, Text(code));
    internal string Text(string code) => $"{code}: candidate_sha={Fixture.Panel.Candidate}; base_sha={Fixture.BaseSha}";
    internal string Line(int cursor, string code) => JsonSerializer.Serialize(new
    {
        cursor, timestamp = Fixture.Panel.Time.UtcNow, goalId = Fixture.GoalId,
        eventType = "GoalEscalated", reason = Text(code)
    });
    internal PanelCaseKey FullReadKey(string triggerId)
    {
        // Read() is the unchanged owner-digest scan, independent of incremental cursors.
        var trigger = Assert.Single(Fixture.Sources.Read(), item => item.TriggerId == triggerId);
        var snapshot = Fixture.Sources.ReadGoal(trigger.GoalId);
        var criteria = ConductorJudgePanelCriteriaAtTrigger.Resolve(snapshot, trigger.RecordedAt);
        return new(snapshot?.Id ?? trigger.GoalId, trigger.CandidateSha, trigger.BaseSha, criteria.Version,
            trigger.TriggerId, trigger.KindToken, Fixture.Packets.Build(trigger, criteria).Text);
    }
    internal void LedgerSql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Fixture.Panel.Root, "panel.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public void Dispose() { Host.Stop(); Fixture.Dispose(); }
}
