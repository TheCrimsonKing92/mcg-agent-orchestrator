using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its migrated database and connections under a unique root.
public sealed class SqliteOpenHumanInputRequestQueryTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OpenRequests_TerminalAndMissingGoals_ReturnsOnlyUnansweredRows()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var repository = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var statuses = new[] { GoalStatus.Active, GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Superseded };
            foreach (var status in statuses) kernel.CreateGoal(status.ToString());
            var before = kernel.ExportSnapshot();
            var goals = before.Goals.Select((goal, index) => goal with { Status = statuses[index] }).ToArray();
            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(before with { Goals = goals }));
            var expected = goals.Select(goal => Request(goal.Id, goal.Status.ToString())).Append(
                Request("55555555555555555555555555555555", "Missing goal") with
                {
                    Kind = HumanWaitKind.ProspectiveAcceptanceEvidence, SuggestedDefaultAnswer = "later"
                }).ToArray();
            await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await connection.OpenAsync();
                foreach (var request in expected.Concat([
                    Request(goals[0].Id, "Answered") with { IsCompleted = true, Answer = "yes", AnsweredAt = RequestedAt },
                    Request(goals[0].Id, "Dismissed") with { IsCompleted = true, WasDismissed = true }
                ]))
                    await InsertAsync(connection, request);

                // Poison the goal snapshots: request discovery must not deserialize them.
                await using var poison = connection.CreateCommand();
                poison.CommandText = "UPDATE goals SET snapshot_json = '{invalid'";
                Assert.Equal(goals.Length, await poison.ExecuteNonQueryAsync());
            }

            IOrchestratorStateQueries queries = SqliteOrchestratorStateRepository.OpenReadOnly(db);
            var actual = await queries.ListOpenHumanInputRequestsAsync();

            Assert.Equal(expected.OrderBy(request => request.Id), actual.OrderBy(request => request.Id));
            Assert.All(actual, request => Assert.False(request.IsCompleted));
            Assert.Contains(actual, request => request.GoalId == expected[^1].GoalId);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task OpenRequests_MalformedRequest_PropagatesJsonFailure()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO human_input_requests(id, goal_id, snapshot_json) VALUES('bad', 'missing', '{invalid')";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(db);
            await Assert.ThrowsAsync<JsonException>(() => repository.ListOpenHumanInputRequestsAsync());
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static HumanInputRequestSnapshot Request(string goalId, string question) =>
        new(Guid.NewGuid().ToString("N"), goalId, null, question, RequestedAt);

    private static async Task InsertAsync(SqliteConnection connection, HumanInputRequestSnapshot request)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO human_input_requests(id, goal_id, snapshot_json) VALUES($id, $goal, $snapshot)";
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$goal", request.GoalId);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(request));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
