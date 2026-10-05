using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class SqliteOrchestratorStateRepositoryGoalCreationScopeTests
{
    [Fact]
    public async Task GoalCreationScope_ClaimsAndLegacyLinks_LoadsOnlyConsultedGoals()
    {
        var db = Path.Combine(CreateTempDirectory(), "state.db");
        var repository = CreateMigratedStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var unrelated = kernel.CreateGoal("Unrelated goal");
        var dependency = kernel.CreateGoal("Explicit dependency");
        var owner = kernel.CreateGoal("Claim owner without a legacy link");
        var legacy = kernel.CreateGoal("Legacy linked goal without a claim");
        var itemA = Guid.NewGuid().ToString("n");
        var itemB = Guid.NewGuid().ToString("n");
        kernel.SetGoalSourceBacklogItemLink(legacy.Id, itemB, SourceBacklogCoverage.Full);
        await repository.SaveAsync(kernel);
        using (var conn = OpenState(db))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO source_backlog_claims (backlog_item_id, owner_goal_id, coverage, version, updated_at)
                VALUES ($item, $owner, 'full', 1, $updated)
                """;
            cmd.Parameters.AddWithValue("$item", itemA);
            cmd.Parameters.AddWithValue("$owner", owner.Id.Value);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var scope = new GoalCreationLoadScope([GoalId.New(), dependency.Id], [itemA, itemB]);
        var delegateRan = false;

        await repository.TransactGoalCreationWithOutboxAsync(scope, (loaded, _) =>
        {
            delegateRan = true;
            Assert.DoesNotContain(loaded.Goals, goal => goal.Id == unrelated.Id);
            Assert.Equal(new[] { dependency.Id.Value, owner.Id.Value, legacy.Id.Value }.OrderBy(id => id),
                loaded.Goals.Select(goal => goal.Id.Value).OrderBy(id => id));
            return Task.FromResult((ShouldSave: false, Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
        });

        Assert.True(delegateRan);
    }

    [Fact]
    public async Task GoalCreationScope_UnchangedLoadedGoal_PreservesGoalAndRequestRows()
    {
        var db = Path.Combine(CreateTempDirectory(), "state.db");
        var repository = CreateMigratedStateRepository(db);
        var kernel = new AgentOrchestratorKernel();
        var dependency = kernel.CreateGoal("Loaded dependency with a human request");
        var request = kernel.RequestHumanInput(dependency.Id, null, "Choose dependency option");
        await repository.SaveAsync(kernel);
        using (var conn = OpenState(db))
        {
            var stored = ReadRows(conn, dependency.Id);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE goals SET snapshot_json = $goalJson WHERE id = $goal;
                UPDATE human_input_requests SET snapshot_json = $requestJson WHERE id = $request;
                """;
            cmd.Parameters.AddWithValue("$goal", dependency.Id.Value);
            cmd.Parameters.AddWithValue("$request", request.Id.Value);
            cmd.Parameters.AddWithValue("$goalJson", Indent(stored.GoalJson));
            cmd.Parameters.AddWithValue("$requestJson", Indent(stored.RequestJson));
            Assert.Equal(2, cmd.ExecuteNonQuery());
        }
        StoredRows before;
        using (var conn = OpenState(db))
            before = ReadRows(conn, dependency.Id);
        var scope = new GoalCreationLoadScope([dependency.Id], []);
        var delegateRan = false;

        await repository.TransactGoalCreationWithOutboxAsync(scope, (loaded, _) =>
        {
            delegateRan = true;
            Assert.Equal(dependency.Id, Assert.Single(loaded.Goals).Id);
            Assert.Equal(request.Id, Assert.Single(loaded.HumanInputRequests).Id);
            return Task.FromResult((ShouldSave: true, Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
        });

        Assert.True(delegateRan);
        using var afterConnection = OpenState(db);
        Assert.Equal(before, ReadRows(afterConnection, dependency.Id));
    }

    private static SqliteConnection OpenState(string db)
    {
        var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        return conn;
    }

    private static string Indent(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static StoredRows ReadRows(SqliteConnection conn, GoalId goalId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.version, g.updated_at, g.snapshot_json, r.snapshot_json
            FROM goals g JOIN human_input_requests r ON r.goal_id = g.id WHERE g.id = $goal
            """;
        cmd.Parameters.AddWithValue("$goal", goalId.Value);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new StoredRows(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private sealed record StoredRows(long Version, string UpdatedAt, string GoalJson, string RequestJson);
}
