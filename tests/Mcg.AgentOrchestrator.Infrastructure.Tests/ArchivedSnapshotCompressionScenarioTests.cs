using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each scenario owns its workspace, store and journal; flags and time are explicit inputs.
public sealed class ArchivedSnapshotCompressionScenarioTests
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
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    [Fact]
    public async Task DueCadence_Enabled_CompressesLegacyRowsAndPreservesSnapshots()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync();
        var activeBefore = fixture.GoalTexts()[goals[5].Id];
        var result = fixture.Run(Now, enabled: true);
        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Contains("archived=2", Assert.Single(fixture.Details("goal-snapshot-archive")));
        Assert.Single(File.ReadAllLines(fixture.Workspace.ConductEventsLogPath), line => line.Contains("GOAL_SNAPSHOT_ARCHIVE"));
        Assert.StartsWith("ARCHIVED_SNAPSHOT_COMPRESSION status=completed rows=3 ",
            Assert.Single(fixture.Details("archived-snapshot-compression")));
        var afterFirst = fixture.ArchiveRows();
        Assert.Equal(5, afterFirst.Count);
        Assert.All(afterFirst.Values, row => Assert.Equal("blob", row.Type));
        await AssertReadersAsync(fixture.Repository, goals.Take(5));
        Assert.Equal(Encoding.UTF8.GetBytes(activeBefore), Encoding.UTF8.GetBytes(fixture.GoalTexts()[goals[5].Id]));

        var second = fixture.Run(Now.AddHours(25), enabled: true);
        Assert.True(second.Attempted);
        Assert.False(second.Failed);
        var details = fixture.Details("archived-snapshot-compression");
        Assert.Equal(2, details.Count);
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=completed rows=0 sourceBytes=0 storedBytes=0 batches=1", details[1]);
        var afterSecond = fixture.ArchiveRows();
        Assert.Equal(afterFirst.Keys, afterSecond.Keys);
        foreach (var id in afterFirst.Keys)
            AssertArchiveEqual(afterFirst[id], afterSecond[id]);
        await AssertReadersAsync(fixture.Repository, goals.Take(5));
        Assert.Equal(activeBefore, fixture.GoalTexts()[goals[5].Id]);
    }

    [Fact]
    public async Task DueCadence_CompressionFailure_PreservesMaintenanceResult()
    {
        using var failed = new Fixture();
        using var completed = new Fixture();
        await failed.SeedAsync();
        await completed.SeedAsync();
        var failedCalls = 0;
        var completedCalls = 0;
        var failure = failed.Run(Now, enabled: true, _ =>
        {
            failedCalls++;
            throw new InvalidOperationException("fixture");
        });
        var success = completed.Run(Now, enabled: true, _ =>
        {
            completedCalls++;
            return new(0, 0, 0);
        });
        Assert.Equal(1, failedCalls);
        Assert.Equal(1, completedCalls);
        Assert.True(success.Attempted);
        Assert.False(success.Failed);
        Assert.Equal(success.Attempted, failure.Attempted);
        Assert.Equal(success.Failed, failure.Failed);
        Assert.Equal(success.Reason, failure.Reason);
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=failed rows=0 sourceBytes=0 storedBytes=0 batches=1 exception=InvalidOperationException",
            Assert.Single(failed.Details("archived-snapshot-compression")));
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=completed rows=0 sourceBytes=0 storedBytes=0 batches=1",
            Assert.Single(completed.Details("archived-snapshot-compression")));
    }

    [Fact]
    public async Task DueCadence_Disabled_PreservesTextBytesAndNeverCallsCompression()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync();
        var legacyBefore = fixture.ArchiveRows();
        var hotBefore = fixture.GoalTexts();
        var calls = 0;
        var result = fixture.Run(Now, enabled: false, _ =>
        {
            calls++;
            return new(1, 1, 1);
        });
        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Equal(0, calls);
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=disabled rows=0 sourceBytes=0 storedBytes=0 batches=0",
            Assert.Single(fixture.Details("archived-snapshot-compression")));
        Assert.Contains("archived=2", Assert.Single(fixture.Details("goal-snapshot-archive")));
        var after = fixture.ArchiveRows();
        Assert.Equal(5, after.Count);
        Assert.All(after.Values, row => Assert.Equal("text", row.Type));
        foreach (var (id, row) in legacyBefore)
            AssertArchiveEqual(row, after[id]);
        foreach (var goal in goals.Skip(3).Take(2))
            Assert.Equal(Encoding.UTF8.GetBytes(hotBefore[goal.Id]), after[goal.Id].Bytes);
        Assert.Equal(hotBefore[goals[5].Id], fixture.GoalTexts()[goals[5].Id]);
        await AssertReadersAsync(fixture.Repository, goals.Take(5));
    }

    private static async Task AssertReadersAsync(SqliteOrchestratorStateRepository repository,
        IEnumerable<GoalSnapshot> goals)
    {
        foreach (var goal in goals)
        {
            var id = new GoalId(goal.Id);
            Assert.Equal(Json(goal), Json(await repository.LoadGoalAsync(id)));
            var kernel = await repository.LoadGoalsAsync([id]);
            Assert.Equal(Json(goal), Json(kernel.ExportGoalSnapshot(id)));
        }
    }

    private static void AssertArchiveEqual(ArchiveRow expected, ArchiveRow actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.ArchivedAt, actual.ArchivedAt);
    }

    private sealed record ArchiveRow(string Type, byte[] Bytes, string ArchivedAt);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "archive-compression-scenarios", Guid.NewGuid().ToString("N"));
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
        }

        public async Task<GoalSnapshot[]> SeedAsync()
        {
            var goals = Enumerable.Range(0, 6).Select(index =>
            {
                var id = $"{index + 1:x8}{new string('0', 24)}";
                var at = Now.AddDays(-4);
                var task = new TaskSnapshot(TaskId.New().Value, "Archive scenario work", AgentRole.Developer,
                    WorkTaskStatus.Completed, null, null, null, null, null, null, WorkerBuildInvocations: []);
                var snapshot = new GoalSnapshot(id, "Daily archive compression", index == 5 ? GoalStatus.Active : GoalStatus.Completed,
                    [task], Enumerable.Range(0, 200).Select(entry => new ProgressEventSnapshot(id, null,
                        ProgressKind.GoalPolicyDecision, "Repeated terminal evidence é 中 \U0001F600", at.AddSeconds(entry))).ToArray());
                return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
                    .ExportGoalSnapshot(new GoalId(id));
            }).ToArray();
            await SaveOldAsync(goals.Take(3).ToArray());
            var texts = GoalTexts();
            Assert.Equal(3, await Repository.ArchiveTerminalGoalSnapshotsAsync(Now.AddDays(-2), compressSnapshots: true));
            foreach (var goal in goals.Take(3))
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $text WHERE goal_id = $id";
                command.Parameters.AddWithValue("$text", texts[goal.Id]);
                command.Parameters.AddWithValue("$id", goal.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            await SaveOldAsync(goals.Skip(3).ToArray());
            var archive = ArchiveRows();
            Assert.Equal(3, archive.Count);
            Assert.All(archive.Values, row => Assert.Equal("text", row.Type));
            Assert.Equal(6, GoalTexts().Count);
            return goals;
        }

        private async Task SaveOldAsync(GoalSnapshot[] goals)
        {
            await Repository.SaveGoalSnapshotsAsync(goals);
            await Repository.EnsureTerminalOpenWorkIndexAsync();
            foreach (var goal in goals)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE goals SET updated_at = $at WHERE id = $id";
                command.Parameters.AddWithValue("$at", goal.Timeline.Max(entry => entry.OccurredAt).ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$id", goal.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
        }

        public RunEventMaintenanceCadenceResult Run(DateTimeOffset now, bool enabled,
            Func<long, GoalSnapshotArchiveRecompressionResult>? compression = null) =>
            RunEventMaintenanceCadence.TryRunIfDue(Workspace.RunEventStorePath, Workspace.ConductEventsLogPath, () => now,
                maintenanceOperation: (_, _) => CompletedMaintenance,
                workspace: Workspace,
                artifactRetentionOperation: (_, _, _, _) => SuccessfulSweep,
                archivedSnapshotCompressionOperation: compression,
                archivedSnapshotCompressionEnabled: enabled);

        public List<string> Details(string kind)
        {
            var details = new List<string>();
            foreach (var line in File.ReadAllLines(Workspace.ConductEventsLogPath))
            {
                using var record = JsonDocument.Parse(line);
                if (record.RootElement.GetProperty("eventKind").GetString() == kind)
                    details.Add(record.RootElement.GetProperty("detail").GetString()!);
            }
            return details;
        }

        public Dictionary<string, string> GoalTexts()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, snapshot_json FROM goals ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, string>();
            while (reader.Read()) rows.Add(reader.GetString(0), reader.GetString(1));
            return rows;
        }

        public Dictionary<string, ArchiveRow> ArchiveRows()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, typeof(snapshot_json), CAST(snapshot_json AS BLOB), archived_at FROM goal_snapshot_archive ORDER BY goal_id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, ArchiveRow>();
            while (reader.Read()) rows.Add(reader.GetString(0), new(reader.GetString(1), (byte[])reader.GetValue(2), reader.GetString(3)));
            return rows;
        }

        private SqliteConnection Open() => StateDbConnectionFactory.Open(Workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
