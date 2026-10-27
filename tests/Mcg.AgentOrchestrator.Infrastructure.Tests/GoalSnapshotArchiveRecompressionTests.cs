using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns a migrated temporary database and never changes the environment.
public sealed class GoalSnapshotArchiveRecompressionTests
{
    private static readonly DateTimeOffset Now = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    [Fact]
    public async Task Recompress_MixedArchive_PreservesReadersAndBoundsOrderedWork()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync(5);
        var originalText = fixture.GoalTexts();
        Assert.Equal(5, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
            Now.AddDays(-2), compressSnapshots: true));
        var compressed = fixture.ArchiveRows();
        Assert.Equal(5, compressed.Count);
        Assert.All(compressed.Values, row =>
        {
            Assert.Equal("blob", row.Type);
            Assert.Equal("MGB1"u8.ToArray(), row.Bytes[..4]);
        });

        foreach (var goal in goals.Take(3))
            fixture.SetArchive(goal.Id, originalText[goal.Id]);
        var mixed = fixture.ArchiveRows();
        Assert.Equal(3, mixed.Values.Count(row => row.Type == "text"));
        Assert.Equal(2, mixed.Values.Count(row => row.Type == "blob"));
        var baseline = goals.ToDictionary(goal => goal.Id, Json);
        await AssertReadersAsync(fixture.Repository, baseline);
        var hotRows = fixture.AllGoalRows();

        var first = await fixture.Repository.RecompressArchivedGoalSnapshotsAsync(1);
        Assert.Equal(1, first.Rows);
        Assert.Equal(Encoding.UTF8.GetByteCount(originalText[goals[0].Id]), first.SourceBytes);
        var afterFirst = fixture.ArchiveRows();
        Assert.Equal("blob", afterFirst[goals[0].Id].Type);
        Assert.Equal(afterFirst[goals[0].Id].Bytes.LongLength, first.StoredBytes);
        foreach (var goal in goals.Skip(1))
            AssertArchiveEqual(mixed[goal.Id], afterFirst[goal.Id]);
        Assert.Equal(2, afterFirst.Values.Count(row => row.Type == "text"));
        Assert.Equal(hotRows, fixture.AllGoalRows());
        await AssertReadersAsync(fixture.Repository, baseline);

        var second = await fixture.Repository.RecompressArchivedGoalSnapshotsAsync();
        Assert.Equal(2, second.Rows);
        Assert.Equal(goals.Skip(1).Take(2).Sum(goal => (long)Encoding.UTF8.GetByteCount(originalText[goal.Id])),
            second.SourceBytes);
        Assert.True((first.StoredBytes + second.StoredBytes) * 4 <= first.SourceBytes + second.SourceBytes);
        var afterSecond = fixture.ArchiveRows();
        Assert.All(afterSecond.Values, row => Assert.Equal("blob", row.Type));
        Assert.Equal(afterSecond[goals[1].Id].Bytes.LongLength + afterSecond[goals[2].Id].Bytes.LongLength,
            second.StoredBytes);
        foreach (var goal in goals)
        {
            Assert.Equal(mixed[goal.Id].ArchivedAt, afterSecond[goal.Id].ArchivedAt);
            if (goal.Id == goals[0].Id || mixed[goal.Id].Type == "blob")
                AssertArchiveEqual(afterFirst[goal.Id], afterSecond[goal.Id]);
        }
        Assert.Equal(hotRows, fixture.AllGoalRows());
        await AssertReadersAsync(fixture.Repository, baseline);

        Assert.Equal(new(0, 0, 0), await fixture.Repository.RecompressArchivedGoalSnapshotsAsync());
        var afterThird = fixture.ArchiveRows();
        Assert.Equal(afterSecond.Keys, afterThird.Keys);
        foreach (var id in afterSecond.Keys)
            AssertArchiveEqual(afterSecond[id], afterThird[id]);
        Assert.Equal(hotRows, fixture.AllGoalRows());
        await AssertReadersAsync(fixture.Repository, baseline);
    }

    [Fact]
    public async Task LoadGoal_CorruptOrMissingArchive_ThrowsOrReturnsExactStub()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync(2);
        Assert.Equal(2, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
            Now.AddDays(-2), compressSnapshots: true));
        fixture.SetArchive(goals[0].Id,
            "MGB1"u8.ToArray().Concat(Enumerable.Repeat((byte)0xFF, 16)).ToArray());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Repository.LoadGoalAsync(new GoalId(goals[0].Id)));
        Assert.Contains("goal_snapshot_archive", error.Message);

        fixture.DeleteArchive(goals[1].Id);
        var stub = await fixture.Repository.LoadGoalAsync(new GoalId(goals[1].Id));
        Assert.NotNull(stub);
        Assert.True(stub.IsMetadataOnly);
        Assert.Empty(stub.Tasks);
        Assert.Equal(fixture.GoalTexts()[goals[1].Id], Json(stub));
    }

    [Fact]
    public async Task Archive_DefaultSetting_KeepsLegacyTextByteIdentical()
    {
        using var fixture = new Fixture();
        var goals = await fixture.SeedAsync(1);
        var original = fixture.GoalTexts()[goals[0].Id];
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now.AddDays(-2)));
        var row = Assert.Single(fixture.ArchiveRows()).Value;
        Assert.Equal("text", row.Type);
        Assert.Equal(Encoding.UTF8.GetBytes(original), row.Bytes);
        await AssertReadersAsync(fixture.Repository, goals.ToDictionary(goal => goal.Id, Json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Recompress_NonpositiveBudget_ThrowsWithoutChangingRows(long budget)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(1);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now.AddDays(-2)));
        var before = fixture.ArchiveRows();
        var hot = fixture.AllGoalRows();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Repository.RecompressArchivedGoalSnapshotsAsync(budget));
        AssertArchiveEqual(Assert.Single(before).Value, Assert.Single(fixture.ArchiveRows()).Value);
        Assert.Equal(hot, fixture.AllGoalRows());
    }

    private static async Task AssertReadersAsync(SqliteOrchestratorStateRepository repository,
        Dictionary<string, string> baseline)
    {
        foreach (var (goalId, expected) in baseline)
        {
            var id = new GoalId(goalId);
            Assert.Equal(expected, Json(await repository.LoadGoalAsync(id)));
            Assert.Equal(expected, Json(Assert.Single(await repository.FindGoalSnapshotsByIdPrefixAsync(goalId[..8], 2))));
            Assert.Equal(expected, await repository.TransactGoalAsync<string>(id,
                (snapshot, _) => Task.FromResult((false, snapshot, Json(snapshot)))));
            Assert.Equal(expected, await repository.TransactGoalStateAsync<string>(id,
                (state, _) => Task.FromResult((false, state, Json(state!.Goal)))));
            var kernel = await repository.LoadGoalsAsync([id]);
            Assert.Equal(expected, Json(kernel.ExportGoalSnapshot(id)));
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
        private readonly string _root = Path.Combine(Path.GetTempPath(), "archive-recompression-tests", Guid.NewGuid().ToString("N"));
        private readonly string _databasePath;
        public SqliteOrchestratorStateRepository Repository { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _databasePath = Path.Combine(_root, "state.db");
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(_databasePath);
        }

        public async Task<GoalSnapshot[]> SeedAsync(int count)
        {
            var goals = Enumerable.Range(0, count).Select(index =>
            {
                var id = $"{index + 1:x8}{new string('0', 24)}";
                var at = Now.AddDays(-4);
                var task = new TaskSnapshot(TaskId.New().Value, "Archive work", AgentRole.Developer,
                    WorkTaskStatus.Completed, null, null, null, null, null, null, WorkerBuildInvocations: []);
                var snapshot = new GoalSnapshot(id, "Archive recompression é 中 \U0001F600", GoalStatus.Completed, [task],
                    Enumerable.Range(0, 200).Select(entry => new ProgressEventSnapshot(id, null,
                        ProgressKind.GoalPolicyDecision, "Repeated terminal goal evidence", at.AddSeconds(entry))).ToArray());
                return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
                    .ExportGoalSnapshot(new GoalId(id));
            }).ToArray();
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
            return goals;
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

        public string AllGoalRows()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM goals ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            return Json(rows);
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

        public void SetArchive(string id, object value)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $value WHERE goal_id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void DeleteArchive(string id)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM goal_snapshot_archive WHERE goal_id = $id";
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private SqliteConnection Open() => StateDbConnectionFactory.Open(_databasePath, StateDbConnectionProfile.ReadWrite);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
