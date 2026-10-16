using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its workspace and SQLite databases.
public sealed class ExperimentMemberStoreTests
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T00:00:00Z");

    [Fact]
    public void AddPair_PersistsImmutableEventsWithoutChangingVersionedRecords()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(ExperimentAddPairCommandTests.Spec()).GetAwaiter().GetResult();
            Assert.Empty(store.ListMemberEventsAsync(record.Id).GetAwaiter().GetResult());
            Assert.Empty(ExperimentStore.OpenReadOnly(workspace.ExperimentStorePath)
                .ListMemberEventsAsync(record.Id).GetAwaiter().GetResult());
            var before = Snapshot(workspace.ExperimentStorePath);
            var events = Add(store, record.Id, "first", "second");
            Assert.Equal(2, events.Count);
            Assert.Equal(events[0].PairId, events[1].PairId);
            Assert.NotEqual(events[0].EventId, events[1].EventId);
            Assert.All(events, member =>
            {
                Assert.Equal(32, member.PairId.Length);
                Assert.Equal(32, member.EventId.Length);
                Assert.Equal(At, member.AddedAt);
                Assert.Equal("matched briefs", member.Reason);
            });
            Assert.Equal("baseline", events[0].Arm);
            Assert.Equal("first", events[0].GoalId);
            Assert.Equal(new string('a', 64), events[0].BriefSha256);
            Assert.Equal("comparison", events[1].Arm);
            Assert.Equal("second", events[1].GoalId);
            Assert.Equal(new string('b', 64), events[1].BriefSha256);
            Assert.Equal(before, Snapshot(workspace.ExperimentStorePath));
            Assert.Equal(events, new ExperimentStore(workspace.ExperimentStorePath)
                .ListMemberEventsAsync(record.Id).GetAwaiter().GetResult());
            Assert.Equal(events, ExperimentStore.OpenReadOnly(workspace.ExperimentStorePath)
                .ListMemberEventsAsync(record.Id).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void AddPair_DecidedExperimentRefusesWithoutCreatingEvents()
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(ExperimentAddPairCommandTests.Spec()).GetAwaiter().GetResult();
            store.DecideAsync(record.Id, ExperimentOutcomeState.Inconclusive, "receipt", "stop").GetAwaiter().GetResult();
            var error = Assert.Throws<InvalidOperationException>(() => Add(store, record.Id, "first", "second"));
            Assert.Contains("rule experiment-open:", error.Message);
            Assert.Equal(0, EventCount(workspace.ExperimentStorePath));
        });
    }

    [Theory]
    [InlineData("same-arm")]
    [InlineData("other-arm")]
    [InlineData("other-events")]
    [InlineData("spec-baseline")]
    [InlineData("spec-comparison")]
    public void AddPair_OpenMembershipRefusesAtomically(string membership)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var record = store.AddAsync(ExperimentAddPairCommandTests.Spec()).GetAwaiter().GetResult();
            var owner = membership.StartsWith("spec", StringComparison.Ordinal)
                ? store.AddAsync(ExperimentAddPairCommandTests.Spec() with { Baseline = new(ExperimentBaselineKind.GoalCohort,
                    BaselineGoalIds: membership == "spec-baseline" ? [" FIR "] : [],
                    ComparisonGoalIds: membership == "spec-comparison" ? [" FIR "] : []) }).GetAwaiter().GetResult()
                : membership == "other-events" ? store.AddAsync(ExperimentAddPairCommandTests.Spec()).GetAwaiter().GetResult() : record;
            if (!membership.StartsWith("spec", StringComparison.Ordinal)) Add(store, owner.Id, "first", "second");
            var before = EventCount(workspace.ExperimentStorePath);
            var error = Assert.Throws<InvalidOperationException>(() => membership == "other-arm"
                ? Add(store, record.Id, "new", "FIRST") : Add(store, record.Id, "FIRST", "new"));
            Assert.Contains("rule single-membership:", error.Message);
            Assert.Equal(before, EventCount(workspace.ExperimentStorePath));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddPair_MembershipInDecidedExperimentAllowsReuse(bool recorded)
    {
        CliCommandTestsExperiments.WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var spec = ExperimentAddPairCommandTests.Spec();
            var old = store.AddAsync(recorded ? spec : spec with { Baseline = spec.Baseline with
                { BaselineGoalIds = ["first"], ComparisonGoalIds = ["second"] } }).GetAwaiter().GetResult();
            if (recorded) Add(store, old.Id, "first", "second");
            store.DecideAsync(old.Id, ExperimentOutcomeState.Inconclusive, "receipt", "stop").GetAwaiter().GetResult();
            var current = store.AddAsync(spec).GetAwaiter().GetResult();
            Assert.Equal(2, Add(store, current.Id, "first", "second").Count);
        });
    }

    internal static IReadOnlyList<ExperimentMemberEvent> Add(ExperimentStore store, string id, string baseline, string comparison) =>
        store.AddMemberPairAsync(id, baseline, new string('a', 64), comparison, new string('b', 64),
            "  matched briefs  ", At).GetAwaiter().GetResult();

    internal static int EventCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='experiment_member_events'";
        if (Convert.ToInt32(command.ExecuteScalar()) == 0) return 0;
        command.CommandText = "SELECT COUNT(*) FROM experiment_member_events";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Snapshot(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        var rows = new List<string>();
        foreach (var sql in new[] { "SELECT * FROM store_schema_versions", "SELECT * FROM experiments ORDER BY id",
            "SELECT sql FROM sqlite_master WHERE name='experiments'" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        }
        return string.Join("\n", rows);
    }
}
