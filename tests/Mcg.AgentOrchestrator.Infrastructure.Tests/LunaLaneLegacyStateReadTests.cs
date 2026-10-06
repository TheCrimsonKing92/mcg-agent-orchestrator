using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns a unique temporary database with non-pooled connections.
public sealed class LunaLaneLegacyStateReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(LunaLaneLegacyStateReadTests), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Load_LegacyDispatchJson_ReadsLunaKindWithoutRewritingHistory()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "state.db");
        StateDbMigrations.EnsureUpToDate(database);
        var repository = new SqliteOrchestratorStateRepository(database);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Update one label", AgentRole.Developer);
        var goal = kernel.CreateGoal("Legacy lane state", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-luna", "codex exec", _root, DateTimeOffset.Parse("2026-10-06T12:00:00Z"),
            ProviderName: "OpenAI", ModelName: "gpt-6-luna", WorkerProviderKind: ProviderKind.OpenAICodexLuna));
        await repository.SaveAsync(kernel);

        string legacyJson;
        using (var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False;"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$id", goal.Id.Value);
            command.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            var currentJson = Assert.IsType<string>(command.ExecuteScalar());
            Assert.Contains("\"WorkerProviderKind\":\"OpenAICodexLuna\"", currentJson, StringComparison.Ordinal);
            Assert.Contains("\"WorkerName\":\"codex-luna\"", currentJson, StringComparison.Ordinal);
            legacyJson = currentJson
                .Replace("\"WorkerProviderKind\":\"OpenAICodexLuna\"", "\"WorkerProviderKind\":\"OpenAICodexSpark\"", StringComparison.Ordinal)
                .Replace("\"WorkerName\":\"codex-luna\"", "\"WorkerName\":\"codex-spark\"", StringComparison.Ordinal);
            Assert.Contains("OpenAICodexSpark", legacyJson, StringComparison.Ordinal);
            Assert.Contains("codex-spark", legacyJson, StringComparison.Ordinal);
            command.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
            command.Parameters.AddWithValue("$json", legacyJson);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var restored = await repository.LoadAsync();
        var dispatch = Assert.IsType<TaskDispatchRecord>(restored.GetTask(goal.Id, task.Id).LastDispatch);
        Assert.Equal(ProviderKind.OpenAICodexLuna, dispatch.WorkerProviderKind);
        Assert.Equal("codex-spark", dispatch.WorkerName);

        using var observer = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False;");
        observer.Open();
        using var read = observer.CreateCommand();
        read.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
        read.Parameters.AddWithValue("$id", goal.Id.Value);
        Assert.Equal(legacyJson, Assert.IsType<string>(read.ExecuteScalar()));
    }

    [Theory]
    [InlineData("codex-spark")]
    [InlineData("CODEX-SPARK")]
    [InlineData("codex-luna")]
    public void ResolveProfile_LegacyOrCurrentName_ReturnsLunaProvider(string name)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile(name);
        Assert.Equal(ProviderKind.OpenAICodexLuna, provider.Identity.Kind);
        Assert.Equal("codex-luna", provider.ProfileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
