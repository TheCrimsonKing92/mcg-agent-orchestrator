using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns a unique database directory and cleans it up.
public sealed class GateTestObligationGoalIdQueryTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task ListsObligationGoalsInWholeLoadOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var writer = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            foreach (var (index, count, offset) in new[] { (3, 2, 0), (2, 0, 0), (1, 1, 0), (0, 1, -1) })
            {
                var goal = kernel.CreateGoal(Id(index), $"Goal {index}");
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with
                {
                    GateTestObligations = Enumerable.Range(0, count).Select(obligation => new GateTestObligation(
                        $"gate-{index}-{obligation}", goal.AuthoritativeBrief.Version + offset,
                        ["RequiredTests"], null, "Acceptance evidence", DateTimeOffset.UnixEpoch)).ToArray()
                });
                await writer.SaveAsync(kernel);
            }
            var reader = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);

            var actual = await reader.ListGateTestObligationGoalIdsAsync();

            Assert.Equal(new[] { Id(3), Id(1), Id(0) }, actual);
            var whole = await reader.LoadAsync();
            Assert.Equal(whole.Goals.Where(goal => goal.GateTestObligations.Count > 0)
                .Select(goal => goal.Id), actual);
            Assert.Equal(new[] { Id(1) }, await reader.ListGateTestObligationGoalIdsAsync([Id(1), Id(2)]));
            Assert.Empty(await reader.ListGateTestObligationGoalIdsAsync([]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("malformed JSON")]
    [InlineData("{}")]
    [InlineData("{\"GateTestObligations\":[]}")]
    [InlineData("{\"GateTestObligations\":null}")]
    [InlineData("{\"GateTestObligations\":\"not an array\"}")]
    public async Task ExcludesRowsWithoutValidNonemptyArrays(string json)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(Id(1), "Projection-only goal");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            await using (var connection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$id", Id(1).Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var reader = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            Assert.Empty(await reader.ListGateTestObligationGoalIdsAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EmptyFilterDoesNotOpenMissingDatabase()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "missing.db");
            var reader = SqliteOrchestratorStateRepository.OpenReadOnly(path);

            Assert.Empty(await reader.ListGateTestObligationGoalIdsAsync([]));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static GoalId Id(int index) => new($"c{index:x7}aaaaaaaaaaaaaaaaaaaaaaaa");
}
