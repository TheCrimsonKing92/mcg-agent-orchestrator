using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every scenario owns its temporary workspace, database and journal, with a fixed clock and injected sweeps.
public sealed class GoalSnapshotArchiveHotTableScenarioTests
{
    private static readonly DateTimeOffset Now = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly RunEventMaintenanceResult CompletedMaintenance = new(
        Deferred: false, DeferredReason: null, ConductorTickRowsDeleted: 0,
        AgedConductorTickRowsDeleted: 0, OversizedConductorTickRowsDeleted: 0,
        DeletedPayloadBytesEstimate: 0, MaxRowsDeletedInTransaction: 0, Duration: TimeSpan.Zero,
        BytesBefore: 0, BytesAfter: 0, VacuumRequested: false, VacuumCompleted: false, VacuumDeferred: false);
    private static readonly StorageRetentionResult SuccessfulSweep = new(0, 0, 0, 0, 0, 0, "fixture-sweep", []);
    private static readonly string[] EligibleNames = ["E1", "E2", "E3", "E4", "V"];
    private static readonly string[] ProtectedNames = ["R", "A", "T", "S", "Q", "B"];

    [Fact]
    public async Task DueCadence_RealStore_DrainsEveryEligibleGoalBeyondOneBatchAndKeepsReaderProtectedGoalsFull()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync();
        var before = fixture.GoalRows();
        var ids = goals.Values.Select(goal => new GoalId(goal.Id)).ToArray();
        var expectedOpen = new[] { new GoalId(goals["T"].Id), new GoalId(goals["B"].Id) }
            .OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedOpen, await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        var questions = await fixture.Repository.ListTerminalOwnerQuestionHoldsAsync(ids);
        Assert.Equal(goals["S"].Id, Assert.Single(questions).GoalId);

        var result = fixture.Run(new GoalSnapshotArchiveDrain(1, 16));

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        var archive = fixture.ArchiveRows();
        Assert.Equal(EligibleNames.Select(name => goals[name].Id).Order(StringComparer.Ordinal),
            archive.Keys.Order(StringComparer.Ordinal));
        foreach (var name in EligibleNames)
            Assert.Equal(before[goals[name].Id].Snapshot, archive[goals[name].Id]);
        var after = fixture.GoalRows();
        Assert.Equal(before.Count, after.Count);
        foreach (var name in ProtectedNames)
            Assert.Equal(before[goals[name].Id], after[goals[name].Id]);
        Assert.Equal("GOAL_SNAPSHOT_ARCHIVE status=completed archived=5 cutoff=2020-02-28T12:00:00.0000000+00:00 ageDays=2",
            fixture.ArchiveDetail());
        Assert.Equal(expectedOpen, await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        Assert.Equal(questions, await fixture.Repository.ListTerminalOwnerQuestionHoldsAsync(ids));
    }

    [Fact]
    public async Task DueCadence_BatchCap_ArchivesOnlyTheOldestEligibleGoals()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync();
        var before = fixture.GoalRows();

        var result = fixture.Run(new GoalSnapshotArchiveDrain(1, 2));

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Equal(new[] { goals["E1"].Id, goals["E2"].Id }.Order(StringComparer.Ordinal),
            fixture.ArchiveRows().Keys.Order(StringComparer.Ordinal));
        var after = fixture.GoalRows();
        foreach (var name in new[] { "E3", "E4", "V" })
            Assert.Equal(before[goals[name].Id], after[goals[name].Id]);
        Assert.Equal("GOAL_SNAPSHOT_ARCHIVE status=completed archived=2 cutoff=2020-02-28T12:00:00.0000000+00:00 ageDays=2",
            fixture.ArchiveDetail());
    }

    [Fact]
    public async Task DrainedGoals_ReadBackIdenticallyThroughHydratedLoads()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync();
        var before = fixture.GoalRows();

        var result = fixture.Run(new GoalSnapshotArchiveDrain(1, 16));

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Equal(5, fixture.ArchiveRows().Count);
        var ids = EligibleNames.Select(name => new GoalId(goals[name].Id)).ToArray();
        var hydratedKernel = await fixture.Repository.LoadGoalsAsync(ids);
        var fullKernel = await fixture.Repository.LoadAsync();
        foreach (var name in EligibleNames)
        {
            var id = new GoalId(goals[name].Id);
            var hydrated = await fixture.Repository.LoadGoalAsync(id);
            Assert.NotNull(hydrated);
            Assert.Equal(before[id.Value].Snapshot, JsonSerializer.Serialize(hydrated, JsonOptions));
            if (name == "V")
                Assert.Equal("Verified", hydrated.CurrentHold?.State);
            Assert.Equal(before[id.Value].Snapshot,
                JsonSerializer.Serialize(hydratedKernel.ExportGoalSnapshot(id), JsonOptions));
            Assert.True(fullKernel.GetGoal(id).IsMetadataOnly);
        }
        foreach (var name in ProtectedNames)
            Assert.False(fullKernel.GetGoal(new GoalId(goals[name].Id)).IsMetadataOnly);
    }

    [Theory]
    [InlineData("steward-owner-question", 0)]
    [InlineData("STEWARD-OWNER-QUESTION", 0)]
    [InlineData("author-owner-question", 0)]
    [InlineData("Verified", 1)]
    [InlineData("WorkspaceReady", 1)]
    [InlineData("acceptance", 1)]
    public async Task TerminalHoldState_KeepsOnlyOwnerQuestionGoalsFull(string state, int expectedArchived)
    {
        using var fixture = new Fixture();
        var goal = MakeSnapshot("hold", GoalStatus.Completed, 3, state);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.SetUpdatedAt(goal.Id, Now.AddDays(-3));
        var before = fixture.GoalRows()[goal.Id];

        Assert.Equal(expectedArchived, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now.AddDays(-2)));

        var after = fixture.GoalRows()[goal.Id];
        if (expectedArchived == 0)
        {
            Assert.Equal(before, after);
            Assert.Empty(fixture.ArchiveRows());
        }
        else
        {
            var stub = Deserialize(after.Snapshot);
            Assert.True(stub.IsMetadataOnly);
            Assert.Empty(stub.Tasks);
            Assert.Empty(stub.Timeline);
            Assert.Equal(before.Snapshot, fixture.ArchiveRows()[goal.Id]);
        }
    }

    private static GoalSnapshot MakeSnapshot(string name, GoalStatus status, double ageDays,
        string? holdState = null, WorkTaskStatus taskStatus = WorkTaskStatus.Completed)
    {
        var id = GoalId.New().Value;
        var at = Now.AddDays(-ageDays);
        var task = new TaskSnapshot(TaskId.New().Value, "Archive scenario work", AgentRole.Developer,
            taskStatus, null, null, null, null,
            new TaskDispatchSnapshot("fixture", "fixture", "fixture", at.AddDays(-1), ResultCommit: "last-commit"), null);
        var snapshot = new GoalSnapshot(id, name, status, [task],
        [
            new ProgressEventSnapshot(id, null, ProgressKind.GoalCreated, "Created", at.AddDays(-2)),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Last evidence", at)
        ], SourceBacklogItemId: "source-item",
            CurrentHold: holdState is null ? null : new GoalHoldSnapshot(name, holdState, "Fixture hold", at));
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
            .ExportGoalSnapshot(new GoalId(id));
    }

    private static GoalSnapshot Deserialize(string json) => JsonSerializer.Deserialize<GoalSnapshot>(json, JsonOptions)!;
    private sealed record GoalRow(string Status, string Objective, string? Source, string UpdatedAt, string Snapshot, long Version);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-snapshot-archive-hot-table-tests", Guid.NewGuid().ToString("N"));
        private readonly OrchestratorWorkspace _workspace;
        public SqliteOrchestratorStateRepository Repository { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(_workspace.SqliteStatePath);
        }

        public async Task<Dictionary<string, GoalSnapshot>> SeedAsync()
        {
            var goals = new Dictionary<string, GoalSnapshot>
            {
                ["E1"] = MakeSnapshot("E1", GoalStatus.Completed, 6),
                ["E2"] = MakeSnapshot("E2", GoalStatus.Cancelled, 5),
                ["E3"] = MakeSnapshot("E3", GoalStatus.Superseded, 4),
                ["E4"] = MakeSnapshot("E4", GoalStatus.Completed, 3.5),
                ["V"] = MakeSnapshot("V", GoalStatus.Completed, 3, "Verified"),
                ["R"] = MakeSnapshot("R", GoalStatus.Completed, 1),
                ["A"] = MakeSnapshot("A", GoalStatus.Active, 3),
                ["T"] = MakeSnapshot("T", GoalStatus.Completed, 3, taskStatus: WorkTaskStatus.Running),
                ["S"] = MakeSnapshot("S", GoalStatus.Completed, 3, "steward-owner-question"),
                ["Q"] = MakeSnapshot("Q", GoalStatus.Completed, 3, "author-owner-question"),
                ["B"] = MakeSnapshot("B", GoalStatus.Completed, 3)
            };
            var buildGoal = goals["B"];
            var task = buildGoal.Tasks[0];
            var at = Now.AddDays(-3);
            var process = new WorkerBuildProcess(123, at, "fixture.exe");
            var invocation = new WorkerBuildInvocation(Guid.NewGuid().ToString("N"), "request", buildGoal.Id, task.Id,
                at, "dispatch", _root, "tree", "scope", ["App.csproj"], "Debug", at.AddHours(1),
                process, process, "admission.db", 1, 1, [new WorkerBuildObservation(WorkerBuildPhase.Requested, at)],
                Outcome: null, CustodyReleased: false);
            goals["B"] = buildGoal with { Tasks = [task with { WorkerBuildInvocations = [invocation] }] };
            await Repository.SaveGoalSnapshotsAsync(goals.Values.ToArray());
            await Repository.EnsureTerminalOpenWorkIndexAsync();
            foreach (var goal in goals.Values)
                SetUpdatedAt(goal.Id, goal.Timeline.Max(entry => entry.OccurredAt));
            var rows = GoalRows();
            Assert.Equal(11, rows.Count);
            Assert.All(rows.Values, row => Assert.False(Deserialize(row.Snapshot).IsMetadataOnly));
            Assert.Empty(ArchiveRows());
            return goals;
        }

        public RunEventMaintenanceCadenceResult Run(GoalSnapshotArchiveDrain drain) =>
            RunEventMaintenanceCadence.TryRunIfDue(_workspace.RunEventStorePath, _workspace.ConductEventsLogPath, () => Now,
                maintenanceOperation: (_, _) => CompletedMaintenance,
                workspace: _workspace,
                artifactRetentionOperation: (_, _, _, _) => SuccessfulSweep,
                goalSnapshotArchiveDrain: drain);

        public string ArchiveDetail()
        {
            var line = Assert.Single(File.ReadAllLines(_workspace.ConductEventsLogPath), line => line.Contains("GOAL_SNAPSHOT_ARCHIVE"));
            using var record = JsonDocument.Parse(line);
            Assert.Equal("goal-snapshot-archive", record.RootElement.GetProperty("eventKind").GetString());
            return record.RootElement.GetProperty("detail").GetString()!;
        }

        public void SetUpdatedAt(string id, DateTimeOffset at)
        {
            using var connection = StateDbConnectionFactory.Open(_workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET updated_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public Dictionary<string, GoalRow> GoalRows()
        {
            using var connection = StateDbConnectionFactory.Open(_workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
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
            using var connection = StateDbConnectionFactory.Open(_workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, snapshot_json FROM goal_snapshot_archive ORDER BY goal_id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, string>();
            while (reader.Read())
                rows.Add(reader.GetString(0), reader.GetString(1));
            return rows;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
