using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every test owns its temporary database and fixed clock.
public sealed class BoardSnapshotArchivedSemanticsTests
{
    [Fact]
    public async Task Snapshot_ArchivedGoalMatchesFullLoadAndReadOnlyRowsStayIdentical()
    {
        var root = Path.Combine(Path.GetTempPath(), "board-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(path);
            var repository = new SqliteOrchestratorStateRepository(path);
            var kernel = new AgentOrchestratorKernel(new FixedClock());
            var live = kernel.CreateGoal("Live goal", [new TaskSpec(TaskId.New(), "Live task", AgentRole.Developer)]);
            kernel.ActivateGoal(live.Id, AgentCatalog.Default().Agents);
            kernel.RequestHumanInput(live.Id, null, "Which behavior should ship?");
            var terminal = CompletedSnapshot("ddd10000dddddddddddddddddddddddd");
            var seed = kernel.ExportSnapshot();
            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(seed with
            {
                Goals = seed.Goals.Append(terminal).ToArray()
            }, new FixedClock()));
            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(
                new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero)));

            var full = await repository.LoadAsync();
            var snapshot = await repository.LoadBoardSnapshotAsync();
            AssertEquivalent(full, snapshot);
            Assert.Equal(2, snapshot.Goals.Count);
            Assert.Single(snapshot.HumanInputRequests);
            Assert.False(snapshot.HumanInputRequests.Single().IsCompleted);
            var archivedId = new GoalId(terminal.Id);
            Assert.True(snapshot.GetGoal(archivedId).IsMetadataOnly);
            Assert.Empty(snapshot.GetGoal(archivedId).Tasks);
            var hydrated = await repository.LoadGoalsAsync([archivedId]);
            Assert.False(hydrated.GetGoal(archivedId).IsMetadataOnly);
            Assert.Single(hydrated.GetGoal(archivedId).Tasks);

            await using var connection = OpenConnection(path);
            await connection.OpenAsync();
            var goalsBefore = await ReadRowsAsync(connection, "SELECT * FROM goals ORDER BY id");
            var archiveBefore = await ReadRowsAsync(connection, "SELECT * FROM goal_snapshot_archive ORDER BY goal_id");
            AssertEquivalent(full, await SqliteOrchestratorStateRepository.OpenReadOnly(path).LoadBoardSnapshotAsync());
            Assert.Equal(goalsBefore, await ReadRowsAsync(connection, "SELECT * FROM goals ORDER BY id"));
            Assert.Equal(archiveBefore, await ReadRowsAsync(connection, "SELECT * FROM goal_snapshot_archive ORDER BY goal_id"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertEquivalent(AgentOrchestratorKernel expected, AgentOrchestratorKernel actual)
    {
        Assert.Equal(expected.Goals.Select(goal => goal.Id), actual.Goals.Select(goal => goal.Id));
        foreach (var goal in expected.Goals)
            Assert.Equal(JsonSerializer.Serialize(expected.ExportGoalSnapshot(goal.Id)),
                JsonSerializer.Serialize(actual.ExportGoalSnapshot(goal.Id)));
        Assert.Equal(expected.HumanInputRequests.Select(request => request.Id),
            actual.HumanInputRequests.Select(request => request.Id));
    }

    private static GoalSnapshot CompletedSnapshot(string id)
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Completed implementation", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId(id), "Archived completed goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "fixture-dispatch", "fixture", clock.UtcNow,
            ProviderName: "OpenAI", ModelName: AgentCatalog.OpenAiSubscriptionModelAlias));
        clock.UtcNow = clock.UtcNow.AddMinutes(3);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("fixture-verify", "fixture", 0, "verified", "", clock.UtcNow));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        return snapshot with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Tasks.Select(item => item with { Status = WorkTaskStatus.Completed }).ToArray()
        };
    }

    private static SqliteConnection OpenConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());

    private static async Task<byte[]> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = reader.IsDBNull(index) ? null :
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
            rows.Add(values);
        }
        return JsonSerializer.SerializeToUtf8Bytes(rows);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
