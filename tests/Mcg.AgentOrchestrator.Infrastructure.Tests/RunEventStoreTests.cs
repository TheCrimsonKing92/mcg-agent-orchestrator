using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RunEventStoreTests
{
    [Xunit.Fact(DisplayName = "DogfoodLogStore_upserts_goal_entries_and_lists_recent")]
    public async Task DogfoodLogStoreUpsertsGoalEntriesAndListsRecent()
    {
        var store = new DogfoodLogStore(Path.Combine(CreateTempDirectory(), "dogfood-log.db"));

        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-1",
            "## 2026-07-02 - First",
            "summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - First\n\nsummary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-1",
            "## 2026-07-02 - First updated",
            "updated summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - First updated\n\nupdated summary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-2",
            "## 2026-07-02 - Second",
            "second summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - Second\n\nsecond summary"));

        var recent = await store.ListRecentAsync();
        var first = await store.GetByGoalIdAsync("goal-1");

        Assert.Equal(2, recent.Count);
        Assert.NotNull(first);
        Assert.Equal("updated summary", first!.Summary);
        AssertEx.Contains(recent, record => record.GoalId == "goal-2");
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_appends_records_without_mutating_prior_events")]
    public async Task SqliteRunEventStoreAppendsRecordsWithoutMutatingPriorEvents()
    {
        var store = new SqliteRunEventStore(TempDb());

        var first = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            "goal-1",
            "conductor:dispatch",
            "Begin",
            "starting",
            null));
        var second = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            "goal-1",
            "conductor:dispatch",
            "Completed",
            "started",
            null));

        var all = await store.ReadSinceAsync();
        var afterFirst = await store.ReadSinceAsync(first.Sequence);

        Assert.True(all.Select(e => e.Sequence).SequenceEqual([first.Sequence, second.Sequence]));
        Assert.Equal("Begin", all[0].Status);
        Assert.Equal("Completed", all[1].Status);
        Assert.Single(afterFirst);
        Assert.Equal(second.Sequence, afterFirst[0].Sequence);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_appends_matching_run_event")]
    public async Task GoalOperationJournalAppendsMatchingRunEvent()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Record journal event");

        GoalOperationJournal.Begin(root, goal, "conductor:dispatch", "Starting subscription dispatch.");

        var store = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"));
        var events = await store.ReadSinceAsync(goalId: goal.Id.Value);
        var evt = Assert.Single(events);
        Assert.Equal(RunEventTypes.GoalOperation, evt.EventType);
        Assert.Equal("conductor:dispatch", evt.Operation);
        Assert.Equal("Begin", evt.Status);
        Assert.True(evt.Detail?.Contains("Starting subscription dispatch", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "ConductorTickPusher_appends_tick_to_run_event_store")]
    public async Task ConductorTickPusherAppendsTickToRunEventStore()
    {
        var db = TempDb();

        ConductorTickPusher.TryRecord(
            db,
            new BatchTickSummary(3, Advanced: 2, Held: 1, Escalated: 0, Retried: 0, Done: 1, WatchSleeping: false)
            {
                ProgressLines = ["TICK tick=3 eligible=4", "TICK_END tick=3 advanced=2 held=1 escalated=0 done=1"]
            });

        var events = await new SqliteRunEventStore(db).ReadSinceAsync();
        var evt = Assert.Single(events);
        Assert.Equal(RunEventTypes.ConductorTick, evt.EventType);
        Assert.Equal("conduct:tick", evt.Operation);
        Assert.True(evt.Detail?.Contains("advanced=2", StringComparison.Ordinal) == true);
        Assert.True(evt.PayloadJson?.Contains("TICK_END tick=3", StringComparison.Ordinal) == true);
    }

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "run-events.db");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-run-events-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
