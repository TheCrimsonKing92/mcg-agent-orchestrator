using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its database and diagnostics, with no environment mutation.
public sealed class GoalSnapshotArchiveTests
{
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void SchemaMigration_CreatesArchiveBeforeAnyArchiveOperation()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.ArchiveTableExists());
        Assert.Empty(fixture.ArchiveRows());
    }

    [Fact]
    public async Task Archive_AlreadyMigratedDatabaseWithoutArchive_CreatesArchiveAndHydratesGoal()
    {
        using var fixture = new Fixture();
        fixture.Execute("DROP TABLE goal_snapshot_archive");
        StateDbMigrations.EnsureUpToDate(fixture.DatabasePath);
        Assert.False(fixture.ArchiveTableExists());
        var snapshot = MakeSnapshot(1, GoalStatus.Completed);
        await fixture.Repository.SaveGoalSnapshotsAsync([snapshot]);
        fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-1));

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));
        Assert.True(fixture.ArchiveTableExists());
        Assert.Equal(Json(snapshot), Json(await fixture.Repository.LoadGoalAsync(new GoalId(snapshot.Id))));
    }

    [Fact]
    public async Task Archive_MixedRows_ChangesOnlyOldTerminalSnapshots()
    {
        using var fixture = new Fixture();
        var old = await SeedMixedAsync(fixture);
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

        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff.ToOffset(TimeSpan.FromHours(5))));
        var after = fixture.GoalRows();
        var archive = fixture.ArchiveRows();
        Assert.Equal(old.Select(g => g.Id).Order(), archive.Keys.Order());
        foreach (var (id, row) in before)
        {
            if (!archive.ContainsKey(id))
            {
                Assert.Equal(row, after[id]);
                continue;
            }
            Assert.Equal(row.Snapshot, archive[id].Snapshot);
            Assert.Equal(row, after[id] with { Snapshot = row.Snapshot });
            Assert.NotEqual(row.Snapshot, after[id].Snapshot);
            var stub = Deserialize(after[id].Snapshot);
            var original = Deserialize(row.Snapshot);
            Assert.True(stub.IsMetadataOnly);
            Assert.Empty(stub.Tasks);
            Assert.Empty(stub.Timeline);
            Assert.Equal(original.Status, stub.Status);
            Assert.Equal(original.Objective, stub.Objective);
            Assert.Equal(original.Timeline[0].OccurredAt, stub.CreatedAt);
            Assert.Equal(original.Timeline[^1].OccurredAt, stub.TerminatedAt);
            Assert.Equal("last-commit", stub.ResultCommit);
        }
        Assert.Equal(otherTables, fixture.OtherTableRows());
        var receipt = JsonDocument.Parse(Assert.Single(File.ReadAllLines(fixture.DiagnosticsPath)));
        using (receipt)
            Assert.Equal(nameof(SqliteOrchestratorStateRepository.ArchiveTerminalGoalSnapshotsAsync),
                receipt.RootElement.GetProperty("operation").GetString());

        // A repeated cutoff leaves recent and boundary rows hot as well.
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));
        Assert.Equal(after.OrderBy(r => r.Key), fixture.GoalRows().OrderBy(r => r.Key));
        Assert.Equal(archive.OrderBy(r => r.Key), fixture.ArchiveRows().OrderBy(r => r.Key));
        Assert.Equal(otherTables, fixture.OtherTableRows());
    }

    [Fact]
    public async Task Reads_ArchivedMixedGoals_PreserveFullSnapshotsAndMetadata()
    {
        using var fixture = new Fixture();
        var old = await SeedMixedAsync(fixture);
        var beforeRows = fixture.GoalRows();
        var beforeMetadata = Json(await fixture.Repository.ListGoalMetadataAsync(true));
        var beforeDefaultMetadata = Json(await fixture.Repository.ListGoalMetadataAsync(false));
        var beforeConduct = Json(await fixture.Repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));

        // A new repository also exercises lazy terminal metadata without a warm cache.
        var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath);
        foreach (var original in old)
        {
            var id = new GoalId(original.Id);
            var expected = Json(original);
            Assert.Equal(expected, Json(await repository.LoadGoalAsync(id)));
            Assert.Equal(expected, Json(Assert.Single(await repository.FindGoalSnapshotsByIdPrefixAsync(original.Id[..8], 2))));
            Assert.Equal(expected, await repository.TransactGoalAsync<string>(id,
                (snapshot, _) => Task.FromResult((false, snapshot, Json(snapshot)))));
            Assert.Equal(expected, await repository.TransactGoalStateAsync<string>(id,
                (state, _) => Task.FromResult((false, state, Json(state!.Goal)))));
            var filteredKernel = await repository.LoadGoalsAsync([id]);
            Assert.Equal(expected, Json(filteredKernel.ExportGoalSnapshot(id)));
            Assert.Equal(beforeRows[original.Id].Snapshot, fixture.ArchiveRows()[original.Id].Snapshot);
        }
        Assert.Equal(beforeMetadata, Json(await repository.ListGoalMetadataAsync(true)));
        Assert.Equal(beforeDefaultMetadata, Json(await repository.ListGoalMetadataAsync(false)));
        Assert.Equal(beforeConduct, Json(await repository.ListConductLoopGoalMetadataAsync()));
    }

    [Fact]
    public async Task FullLoad_ArchivedGoal_SkipsTimelineAndPreservesStubOnSave()
    {
        using var fixture = new Fixture();
        var snapshot = MakeSnapshot(1, GoalStatus.Completed);
        await fixture.Repository.SaveGoalSnapshotsAsync([snapshot]);
        fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-1));
        var reader = new OwnerDigestTimelineReader();
        var since = Cutoff.AddDays(-10);
        var until = Cutoff.AddDays(1);
        Assert.NotEmpty(Assert.Single(reader.Read(fixture.DatabasePath, new Dictionary<string, DateTimeOffset>(), since, until)).Timeline);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));
        var rows = fixture.GoalRows();
        var archive = fixture.ArchiveRows();

        var digestGoal = Assert.Single(reader.Read(fixture.DatabasePath, new Dictionary<string, DateTimeOffset>(), since, until));
        Assert.Equal(snapshot.Id, digestGoal.GoalId);
        Assert.Empty(digestGoal.Timeline);
        var kernel = await fixture.Repository.LoadAsync();
        var id = new GoalId(snapshot.Id);
        Assert.True(kernel.GetGoal(id).IsMetadataOnly);
        Assert.True(kernel.ExportGoalSnapshot(id).IsMetadataOnly);
        Assert.Empty(kernel.ExportSnapshot().Goals);
        await fixture.Repository.SaveAsync(kernel);
        Assert.Equal(rows.OrderBy(r => r.Key), fixture.GoalRows().OrderBy(r => r.Key));
        Assert.Equal(archive.OrderBy(r => r.Key), fixture.ArchiveRows().OrderBy(r => r.Key));
    }

    [Fact]
    public async Task Archive_SecondUpdateFails_RollsBackEntireEligibleSet()
    {
        using var fixture = new Fixture();
        var first = MakeSnapshot(1, GoalStatus.Completed);
        var second = MakeSnapshot(2, GoalStatus.Cancelled);
        await fixture.Repository.SaveGoalSnapshotsAsync([first, second]);
        fixture.SetUpdatedAt(first.Id, Cutoff.AddDays(-1));
        fixture.SetUpdatedAt(second.Id, Cutoff.AddDays(-1));
        fixture.Execute($"""
            CREATE TRIGGER reject_second_archive BEFORE UPDATE OF snapshot_json ON goals
            WHEN NEW.id = '{second.Id}' AND
                (SELECT json_extract(snapshot_json, '$.IsMetadataOnly') FROM goals WHERE id = '{first.Id}') = 1
            BEGIN SELECT RAISE(ABORT, 'first goal already stubbed'); END;
            """);
        var before = fixture.GoalRows();
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("first goal already stubbed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before.OrderBy(r => r.Key), fixture.GoalRows().OrderBy(r => r.Key));
        Assert.True(fixture.ArchiveTableExists());
        Assert.Empty(fixture.ArchiveRows());
    }

    [Fact]
    public async Task Reads_UnbackedMetadataStub_ReturnItWithoutCreatingArchive()
    {
        using var fixture = new Fixture();
        // Exercise an older database whose core migration predates the archive table.
        fixture.Execute("DROP TABLE goal_snapshot_archive");
        var full = MakeSnapshot(1, GoalStatus.Completed);
        await fixture.Repository.SaveGoalSnapshotsAsync([full]);
        var stub = new GoalSnapshot(full.Id, full.Objective, full.Status, [], [],
            IsMetadataOnly: true, CreatedAt: Cutoff.AddDays(-2));
        fixture.SetSnapshot(stub);
        Assert.False(fixture.ArchiveTableExists());
        Assert.Equal(Json(stub), Json(await fixture.Repository.LoadGoalAsync(new GoalId(full.Id))));
        Assert.False(fixture.ArchiveTableExists());
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff.AddDays(10)));
        Assert.Empty(fixture.ArchiveRows());
        Assert.Equal(Json(stub), Json(await fixture.Repository.LoadGoalAsync(new GoalId(full.Id))));
    }

    [Fact]
    public async Task Save_HydratedMutation_KeepsOriginalArchiveAndReadsCurrentRow()
    {
        using var fixture = new Fixture();
        var full = MakeSnapshot(1, GoalStatus.Completed);
        await fixture.Repository.SaveGoalSnapshotsAsync([full]);
        fixture.SetUpdatedAt(full.Id, Cutoff.AddDays(-1));
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));
        var originalArchive = fixture.ArchiveRows();
        var id = new GoalId(full.Id);
        var changed = full with { Objective = "Updated terminal objective" };
        Assert.True(await fixture.Repository.TransactGoalAsync<bool>(id, (snapshot, _) =>
        {
            Assert.Equal(Json(full), Json(snapshot));
            return Task.FromResult((true, (GoalSnapshot?)changed, true));
        }));
        Assert.Equal(Json(changed), Json(await fixture.Repository.LoadGoalAsync(id)));
        fixture.SetUpdatedAt(full.Id, Cutoff.AddDays(-1));
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff.AddDays(1)));
        Assert.Equal(originalArchive.OrderBy(r => r.Key), fixture.ArchiveRows().OrderBy(r => r.Key));
        Assert.Equal(Json(changed), Json(await fixture.Repository.LoadGoalAsync(id)));
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public async Task Archive_NonTerminalTasks_PreservesHotRowsAndSweepCandidates(GoalStatus status)
    {
        using var fixture = new Fixture();
        var nonTerminal = Enum.GetValues<WorkTaskStatus>()
            .Where(value => value is not WorkTaskStatus.Completed and not WorkTaskStatus.Cancelled)
            .Select((value, index) =>
            {
                var snapshot = MakeSnapshot(index + 1, status);
                return snapshot with { Tasks = [snapshot.Tasks[0], snapshot.Tasks[0] with
                    { Id = TaskId.New().Value, Status = value }] };
            }).ToArray();
        var eligible = MakeSnapshot(100, status);
        eligible = eligible with { Tasks = [eligible.Tasks[0], eligible.Tasks[0] with
            { Id = TaskId.New().Value, Status = WorkTaskStatus.Cancelled }] };
        foreach (var snapshot in nonTerminal.Append(eligible))
        {
            // WaitingForHuman tasks must be persisted together with their open requests.
            var requests = snapshot.Tasks
                .Where(task => task.Status == WorkTaskStatus.WaitingForHuman)
                .Select(task => new HumanInputRequestSnapshot(Guid.NewGuid().ToString("N"),
                    snapshot.Id, task.Id, "Continue terminal task cleanup?", Cutoff.AddDays(-2)))
                .ToArray();
            Assert.True(await fixture.Repository.TransactGoalStateAsync<bool>(new GoalId(snapshot.Id),
                (_, _) => Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(snapshot, requests), true))));
        }
        foreach (var snapshot in nonTerminal.Append(eligible))
            fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-1));
        var beforeRows = fixture.GoalRows();
        var beforeCandidates = await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync();
        var beforeOtherTables = fixture.OtherTableRows();
        Assert.Equal(nonTerminal.Select(snapshot => snapshot.Id).Order(),
            beforeCandidates.Select(id => id.Value).Order());

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));

        Assert.Equal(eligible.Id, Assert.Single(fixture.ArchiveRows()).Key);
        var afterRows = fixture.GoalRows();
        foreach (var snapshot in nonTerminal)
            Assert.Equal(beforeRows[snapshot.Id], afterRows[snapshot.Id]);
        Assert.Equal(beforeCandidates.OrderBy(id => id.Value),
            (await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync()).OrderBy(id => id.Value));
        Assert.Equal(beforeOtherTables, fixture.OtherTableRows());
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public async Task Archive_CurrentHolds_PreservesHotRowsAndOwnerQuestions(GoalStatus status)
    {
        using var fixture = new Fixture();
        var ownerQuestion = MakeSnapshot(1, status) with
        {
            CurrentHold = new GoalHoldSnapshot("owner-question", "steward-owner-question",
                "Review terminal goal?", Cutoff.AddDays(-2))
        };
        var otherHold = MakeSnapshot(2, status) with
        {
            CurrentHold = new GoalHoldSnapshot("other-hold", "acceptance", "Review evidence", Cutoff.AddDays(-2))
        };
        var eligible = MakeSnapshot(3, status);
        await fixture.Repository.SaveGoalSnapshotsAsync([ownerQuestion, otherHold, eligible]);
        foreach (var snapshot in new[] { ownerQuestion, otherHold, eligible })
            fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-1));
        var beforeRows = fixture.GoalRows();
        var ids = new[] { new GoalId(ownerQuestion.Id), new GoalId(otherHold.Id), new GoalId(eligible.Id) };
        var beforeQuestions = await fixture.Repository.ListTerminalOwnerQuestionHoldsAsync(ids);
        Assert.Equal(ownerQuestion.Id, Assert.Single(beforeQuestions).GoalId);

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff));

        Assert.Equal(eligible.Id, Assert.Single(fixture.ArchiveRows()).Key);
        var afterRows = fixture.GoalRows();
        Assert.Equal(beforeRows[ownerQuestion.Id], afterRows[ownerQuestion.Id]);
        Assert.Equal(beforeRows[otherHold.Id], afterRows[otherHold.Id]);
        Assert.Equal(beforeQuestions, await fixture.Repository.ListTerminalOwnerQuestionHoldsAsync(ids));
    }

    private static async Task<GoalSnapshot[]> SeedMixedAsync(Fixture fixture)
    {
        var old = new[] { MakeSnapshot(1, GoalStatus.Completed), MakeSnapshot(2, GoalStatus.Cancelled), MakeSnapshot(3, GoalStatus.Superseded) };
        var active = MakeSnapshot(4, GoalStatus.Active);
        var recent = MakeSnapshot(5, GoalStatus.Completed);
        var boundary = MakeSnapshot(6, GoalStatus.Cancelled);
        var otherNonTerminal = new[] { MakeSnapshot(7, GoalStatus.Draft), MakeSnapshot(8, GoalStatus.Verified), MakeSnapshot(9, GoalStatus.Failed) };
        await fixture.Repository.SaveGoalSnapshotsAsync([.. old, active, recent, boundary, .. otherNonTerminal]);
        foreach (var snapshot in old.Append(active).Concat(otherNonTerminal))
            fixture.SetUpdatedAt(snapshot.Id, Cutoff.AddDays(-1));
        fixture.SetUpdatedAt(recent.Id, Cutoff.AddDays(1));
        fixture.SetUpdatedAt(boundary.Id, Cutoff);
        return old;
    }

    private static GoalSnapshot MakeSnapshot(int number, GoalStatus status)
    {
        var id = number.ToString("x8", CultureInfo.InvariantCulture) + new string('0', 24);
        var task = new TaskSnapshot(TaskId.New().Value, "Completed implementation", AgentRole.Developer,
            WorkTaskStatus.Completed, null, null, null, null,
            new TaskDispatchSnapshot("fixture", "fixture", "fixture", Cutoff.AddDays(-2), ResultCommit: "last-commit"), null);
        var snapshot = new GoalSnapshot(id, $"Objective {number}", status, [task],
        [
            new ProgressEventSnapshot(id, null, ProgressKind.GoalCreated, "Created", Cutoff.AddDays(-3)),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Later event", Cutoff.AddDays(-1)),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Last array entry", Cutoff.AddDays(-2))
        ], SourceBacklogItemId: "source-item");
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
            .ExportGoalSnapshot(new GoalId(id));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static GoalSnapshot Deserialize(string json) => JsonSerializer.Deserialize<GoalSnapshot>(json, JsonOptions)!;

    private sealed record GoalRow(string Status, string Objective, string? Source, string UpdatedAt, string Snapshot, long Version);
    private sealed record ArchiveRow(string Snapshot, string ArchivedAt);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-snapshot-archive-tests", Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(_root, "state.db");
        public string DiagnosticsPath => Path.Combine(_root, "writes.jsonl");
        public SqliteOrchestratorStateRepository Repository { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            StateDbMigrations.EnsureUpToDate(DatabasePath);
            Repository = new SqliteOrchestratorStateRepository(DatabasePath, null,
                new SqliteWriteTelemetryOptions
                {
                    DiagnosticsPath = DiagnosticsPath,
                    WarningHoldThreshold = TimeSpan.Zero,
                    HoldDurationSource = _ => () => TimeSpan.FromMilliseconds(1),
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
            // Receipts below cover the archive operation, excluding setup saves.
            if (File.Exists(DiagnosticsPath)) File.Delete(DiagnosticsPath);
        }

        public void SetSnapshot(GoalSnapshot snapshot)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
            command.Parameters.AddWithValue("$id", snapshot.Id);
            command.Parameters.AddWithValue("$json", Json(snapshot));
            Assert.Equal(1, command.ExecuteNonQuery());
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

        public Dictionary<string, ArchiveRow> ArchiveRows()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, snapshot_json, archived_at FROM goal_snapshot_archive ORDER BY goal_id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, ArchiveRow>();
            while (reader.Read()) rows.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2)));
            return rows;
        }

        public bool ArchiveTableExists()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'goal_snapshot_archive'";
            return (long)command.ExecuteScalar()! == 1;
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
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
                }
                result.Add(name, rows.Select(row => Json(row)).Order(StringComparer.Ordinal).ToArray());
            }
            return Json(result);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
