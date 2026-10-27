using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its database and diagnostics, with no environment mutation.
public sealed class GoalSnapshotArchiveTerminalAgeTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task RestampedOldGoals_ArchiveLosslesslyAndLeaveOtherRowsUnchanged()
    {
        using var fixture = new Fixture();
        var old = Enumerable.Range(1, 3).Select(n => MakeSnapshot(n, Cutoff.AddDays(-30))).ToArray();
        var recent = MakeSnapshot(4, Cutoff.AddDays(1));
        await fixture.Repository.SaveGoalSnapshotsAsync([.. old, recent]);
        foreach (var snapshot in old)
            fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(2));
        fixture.SetUpdatedAt(recent.Id, Cutoff.AddDays(-30));
        fixture.Execute("""
            INSERT INTO human_input_requests VALUES ('request', 'unrelated', '{"sentinel":1}');
            INSERT INTO model_fit_history
                (goal_id, task_id, role, provider_name, model_name, outcome, self_rating, timestamp)
                VALUES ('unrelated', 'task', 'Developer', 'fixture', 'fixture', 'passed', 'adequate', 'sentinel');
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
                VALUES ('outbox', 'fixture', '{"sentinel":2}', 'sentinel');
            """);
        var before = fixture.GoalRows();
        var otherTables = fixture.OtherTableRows();
        Assert.All(old, goal => Assert.Equal(Json(goal), before[goal.Id].Snapshot));

        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));

        var after = fixture.GoalRows();
        var archive = fixture.ArchiveRows();
        Assert.Equal(old.Select(g => g.Id).Order(StringComparer.Ordinal), archive.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(before[recent.Id], after[recent.Id]);
        foreach (var original in old)
        {
            var expected = before[original.Id].Snapshot;
            Assert.Equal(expected, archive[original.Id]);
            Assert.Equal(before[original.Id], after[original.Id] with { Snapshot = expected });
            Assert.True(Deserialize(after[original.Id].Snapshot).IsMetadataOnly);
            var id = new GoalId(original.Id);
            Assert.Equal(expected, Json(await fixture.Repository.LoadGoalAsync(id)));
            Assert.Equal(expected, Json(Assert.Single(
                await fixture.Repository.FindGoalSnapshotsByIdPrefixAsync(original.Id[..8], 2))));
            var filtered = await fixture.Repository.LoadGoalsAsync([id]);
            Assert.Equal(expected, Json(filtered.ExportGoalSnapshot(id)));
        }
        Assert.Equal(otherTables, fixture.OtherTableRows());
    }

    [Fact]
    public async Task TerminalInstant_UsesStrictMaximumAndNormalizesOffsets()
    {
        using var fixture = new Fixture();
        var boundary = MakeSnapshot(1, Cutoff);
        var empty = MakeSnapshot(2, Cutoff.AddDays(-1)) with { Timeline = [] };
        var unordered = MakeSnapshot(3, Cutoff.AddDays(1));
        unordered = unordered with
        {
            Timeline = [.. unordered.Timeline, new ProgressEventSnapshot(unordered.Id, null,
                ProgressKind.GoalPolicyDecision, "Earlier array-last event", Cutoff.AddDays(-1))]
        };
        var offset = MakeSnapshot(4, Cutoff.AddMinutes(-1).ToOffset(TimeSpan.FromHours(5)));
        await fixture.Repository.SaveGoalSnapshotsAsync([boundary, empty, unordered, offset]);
        foreach (var snapshot in new[] { boundary, empty, unordered, offset })
            fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-30));
        var before = fixture.GoalRows();
        Assert.Equal(0L, fixture.Scalar("SELECT json_array_length(snapshot_json, '$.Timeline') FROM goals WHERE id = $id", empty.Id));
        using (var stored = JsonDocument.Parse(before[offset.Id].Snapshot))
        {
            var timeline = stored.RootElement.GetProperty("Timeline");
            Assert.EndsWith("+05:00", timeline[timeline.GetArrayLength() - 1].GetProperty("OccurredAt").GetString());
        }
        var events = Deserialize(before[unordered.Id].Snapshot).Timeline;
        Assert.True(events[^1].OccurredAt < events.Max(e => e.OccurredAt));

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));

        Assert.Equal(offset.Id, Assert.Single(fixture.ArchiveRows()).Key);
        var after = fixture.GoalRows();
        foreach (var snapshot in new[] { boundary, empty, unordered })
            Assert.Equal(before[snapshot.Id], after[snapshot.Id]);
    }

    [Fact]
    public async Task ByteBudget_DrainsOldestFirstAcrossBoundedPasses()
    {
        using var fixture = new Fixture();
        // Reverse id order makes an id-only walk observably wrong.
        var goals = Enumerable.Range(0, 5).Select(n => MakeSnapshot(105 - n, Cutoff.AddDays(-5 + n),
            objective: n < 2 ? "Budget goal" : "Budget goal" + new string('x', 64))).ToArray();
        await fixture.Repository.SaveGoalSnapshotsAsync(goals);
        foreach (var goal in goals)
            fixture.SetUpdatedAt(goal.Id, Cutoff.AddDays(2));
        var lengths = fixture.SnapshotLengths();
        var budget = lengths[goals[0].Id] + lengths[goals[1].Id];
        Assert.True(lengths[goals[2].Id] < budget);
        Assert.True(lengths[goals[2].Id] + lengths[goals[3].Id] >= budget);

        Assert.Equal(2, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget));
        AssertArchived(2);
        Assert.Equal(2, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget));
        AssertArchived(4);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget));
        AssertArchived(5);
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget));
        AssertArchived(5);

        void AssertArchived(int count) => Assert.Equal(
            goals.Take(count).Select(g => g.Id).Order(StringComparer.Ordinal),
            fixture.ArchiveRows().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TinyBudget_ArchivesOneWholeOldestGoal()
    {
        using var fixture = new Fixture();
        var oldest = MakeSnapshot(2, Cutoff.AddDays(-2));
        var newer = MakeSnapshot(1, Cutoff.AddDays(-1));
        await fixture.Repository.SaveGoalSnapshotsAsync([newer, oldest]);
        fixture.SetUpdatedAt(oldest.Id, Cutoff.AddDays(2));
        fixture.SetUpdatedAt(newer.Id, Cutoff.AddDays(2));
        var before = fixture.GoalRows();
        Assert.True(fixture.SnapshotLengths()[oldest.Id] > 1);

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, maxSnapshotBytes: 1));

        Assert.Equal(oldest.Id, Assert.Single(fixture.ArchiveRows()).Key);
        Assert.Equal(before[oldest.Id].Snapshot, fixture.ArchiveRows()[oldest.Id]);
        Assert.Equal(before[newer.Id], fixture.GoalRows()[newer.Id]);
    }

    [Fact]
    public async Task EqualTerminalInstants_BreakTiesByOrdinalId()
    {
        using var fixture = new Fixture();
        var goals = new[] { MakeSnapshot(2, Cutoff.AddDays(-1)), MakeSnapshot(1, Cutoff.AddDays(-1)) };
        await fixture.Repository.SaveGoalSnapshotsAsync(goals);
        foreach (var goal in goals)
            fixture.SetUpdatedAt(goal.Id, Cutoff.AddDays(2));

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, maxSnapshotBytes: 1));

        Assert.Equal(goals[1].Id, Assert.Single(fixture.ArchiveRows()).Key);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task NonPositiveBudget_IsRejected(long budget)
    {
        using var fixture = new Fixture();
        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget));
        Assert.Equal("maxSnapshotBytes", exception.ParamName);
    }

    [Fact]
    public async Task FullLoad_AfterDrainingReadsStubsAndKeepsRecentPayloads()
    {
        using var fixture = new Fixture();
        var old = Enumerable.Range(1, 10).Select(n => MakeSnapshot(n, Cutoff.AddDays(-30).AddSeconds(n), 200)).ToArray();
        var recent = new[] { MakeSnapshot(11, Cutoff.AddDays(1)), MakeSnapshot(12, Cutoff.AddDays(2)) };
        await fixture.Repository.SaveGoalSnapshotsAsync([.. old, .. recent]);
        foreach (var goal in old.Concat(recent))
            fixture.SetUpdatedAt(goal.Id, Cutoff.AddDays(2));
        var before = fixture.GoalRows();
        var beforeLengths = fixture.SnapshotLengths();
        Assert.All(old, goal => Assert.Equal(200, Deserialize(before[goal.Id].Snapshot).Timeline.Count));
        var budget = beforeLengths[old[0].Id];
        var total = 0;
        var exhausted = false;
        // Each positive pass removes at least one of ten eligible rows; no timing assertion.
        for (var pass = 0; pass < 11; pass++)
        {
            var count = await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, budget);
            total += count;
            if (count == 0)
            {
                exhausted = true;
                break;
            }
        }
        Assert.True(exhausted, "The archive did not exhaust the ten eligible goals within eleven passes.");
        Assert.Equal(10, total);
        Assert.Equal(old.Select(g => g.Id).Order(StringComparer.Ordinal), fixture.ArchiveRows().Keys.Order(StringComparer.Ordinal));
        var after = fixture.GoalRows();
        var afterLengths = fixture.SnapshotLengths();
        foreach (var goal in old)
        {
            var stub = Deserialize(after[goal.Id].Snapshot);
            Assert.True(stub.IsMetadataOnly);
            Assert.Empty(stub.Timeline);
            Assert.True(afterLengths[goal.Id] < beforeLengths[goal.Id]);
        }
        foreach (var goal in recent)
            Assert.Equal(before[goal.Id], after[goal.Id]);
        Assert.Equal(old.Sum(g => afterLengths[g.Id]) + recent.Sum(g => beforeLengths[g.Id]),
            fixture.Scalar("SELECT SUM(length(snapshot_json)) FROM goals"));

        var kernel = await fixture.Repository.LoadAsync();
        var exported = kernel.ExportSnapshot();
        foreach (var goal in old)
        {
            var id = new GoalId(goal.Id);
            Assert.True(kernel.GetGoal(id).IsMetadataOnly);
            Assert.True(kernel.ExportGoalSnapshot(id).IsMetadataOnly);
            Assert.DoesNotContain(exported.Goals, g => g.Id == goal.Id);
        }
        foreach (var goal in recent)
        {
            Assert.False(kernel.GetGoal(new GoalId(goal.Id)).IsMetadataOnly);
            Assert.Equal(before[goal.Id].Snapshot, Json(Assert.Single(exported.Goals, g => g.Id == goal.Id)));
        }
    }

    private static GoalSnapshot MakeSnapshot(int number, DateTimeOffset terminal, int timelineEntries = 2,
        string objective = "Terminal age fixture")
    {
        var id = number.ToString("x8", CultureInfo.InvariantCulture) + new string('0', 24);
        var task = new TaskSnapshot(id[..24] + "00000001", "Completed implementation", AgentRole.Developer,
            WorkTaskStatus.Completed, null, null, null, null,
            new TaskDispatchSnapshot("fixture", "fixture", "fixture", terminal.AddDays(-1)), null);
        var timeline = Enumerable.Range(0, timelineEntries).Select(n => new ProgressEventSnapshot(id, null,
            n == 0 ? ProgressKind.GoalCreated : ProgressKind.GoalPolicyDecision, "Terminal age event",
            terminal.AddSeconds(n - timelineEntries + 1))).ToArray();
        var snapshot = new GoalSnapshot(id, objective, GoalStatus.Completed, [task], timeline,
            SourceBacklogItemId: "source-item");
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
            .ExportGoalSnapshot(new GoalId(id));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static GoalSnapshot Deserialize(string json) => JsonSerializer.Deserialize<GoalSnapshot>(json, JsonOptions)!;
    private sealed record GoalRow(string Status, string Objective, string? Source, string UpdatedAt, string Snapshot, long Version);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-snapshot-terminal-age-tests", Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(_root, "state.db");
        public SqliteOrchestratorStateRepository Repository { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            StateDbMigrations.EnsureUpToDate(DatabasePath);
            Repository = new SqliteOrchestratorStateRepository(DatabasePath, null,
                new SqliteWriteTelemetryOptions
                {
                    DiagnosticsPath = Path.Combine(_root, "writes.jsonl"),
                    MirrorToConductEventStream = false
                });
        }

        private SqliteConnection Open() => StateDbConnectionFactory.Open(DatabasePath, StateDbConnectionProfile.ReadWrite);

        public void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void SetUpdatedAt(string id, DateTimeOffset at)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET updated_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public long Scalar(string sql, string? id = null)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id);
            return (long)command.ExecuteScalar()!;
        }

        public Dictionary<string, long> SnapshotLengths()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, length(snapshot_json) FROM goals ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, long>();
            while (reader.Read()) rows.Add(reader.GetString(0), reader.GetInt64(1));
            return rows;
        }

        public Dictionary<string, GoalRow> GoalRows()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version FROM goals ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, GoalRow>();
            while (reader.Read())
                rows.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
            return rows;
        }

        public Dictionary<string, string> ArchiveRows()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, snapshot_json FROM goal_snapshot_archive ORDER BY goal_id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, string>();
            while (reader.Read()) rows.Add(reader.GetString(0), GoalSnapshotArchiveCodec.Decode(reader.GetValue(1))!);
            return rows;
        }

        public string OtherTableRows()
        {
            using var connection = Open();
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT IN ('goals', 'goal_snapshot_archive') ORDER BY name";
            var names = new List<string>();
            using (var reader = tables.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            var result = new Dictionary<string, string[]>();
            foreach (var name in names)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
                using var reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(Json(values.Select(value => value is DBNull ? null : value).ToArray()));
                }
                result.Add(name, rows.Order(StringComparer.Ordinal).ToArray());
            }
            return Json(result);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
