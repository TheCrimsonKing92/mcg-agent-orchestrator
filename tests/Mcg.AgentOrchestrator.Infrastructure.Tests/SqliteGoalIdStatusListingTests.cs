using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fact owns its migrated database and all connections under a unique root.
public sealed class SqliteGoalIdStatusListingTests
{
    [Xunit.Fact]
    public async Task ListIdStatuses_MalformedTimeline_PreservesOrderedColumnValues()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var repository = new SqliteOrchestratorStateRepository(db);
            await SeedAsync(repository, db);
            var before = await repository.ListGoalMetadataAsync();
            Assert.Equal(3, before.Count);

            await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE goals SET snapshot_json = $snapshot WHERE id = $id";
                command.Parameters.AddWithValue("$snapshot", "{\"Timeline\":[{\"OccurredAt\":");
                command.Parameters.AddWithValue("$id", before[1].Id);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            // Negative control: the old listing still reaches the corrupted Timeline.
            await Assert.ThrowsAsync<SqliteException>(() => repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
            IOrchestratorStateQueries queries = SqliteOrchestratorStateRepository.OpenReadOnly(db);
            var actual = await queries.ListGoalIdStatusesAsync();

            Assert.Equal(before.Select(ColumnValues), actual.Select(ColumnValues));
            Assert.All(actual, summary =>
            {
                Assert.Null(summary.CreatedAt);
                Assert.Null(summary.Condition);
                Assert.Null(summary.ResultCommit);
                Assert.Null(summary.TerminatedAt);
            });
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public async Task ListMetadata_WellFormedGoals_PreservesTimelineAndConditionValues()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var repository = new SqliteOrchestratorStateRepository(db);
            var kernel = await SeedAsync(repository, db);
            var expected = new List<GoalSummary>();
            await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, status, objective, updated_at FROM goals ORDER BY updated_at DESC";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var goal = Assert.Single(kernel.Goals, goal => goal.Id.Value == reader.GetString(0));
                    expected.Add(new GoalSummary(
                        goal.Id.Value,
                        goal.Status.ToString(),
                        goal.Objective,
                        reader.GetString(3),
                        CreatedAt: goal.Timeline[0].OccurredAt,
                        Condition: goal.Status == GoalStatus.Active &&
                            goal.Tasks.Any(task => task.Status == WorkTaskStatus.Failed)
                                ? GoalLifecycle.ActiveWithFailedTaskCondition
                                : null));
                    Assert.Equal(goal.Status.ToString(), reader.GetString(1));
                    Assert.Equal(goal.Objective, reader.GetString(2));
                }
            }

            Assert.Equal(3, expected.Count);
            Assert.Single(expected, summary => summary.Condition == GoalLifecycle.ActiveWithFailedTaskCondition);
            Assert.Equal(expected, await repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static (string Id, string Status, string Objective, string UpdatedAt) ColumnValues(GoalSummary summary) =>
        (summary.Id, summary.Status, summary.Objective, summary.UpdatedAt);

    private static async Task<AgentOrchestratorKernel> SeedAsync(
        SqliteOrchestratorStateRepository repository, string db)
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal("Created goal");
        var active = kernel.CreateGoal("Active goal with failed work", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)
        ]);
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(active.Id, active.Tasks.Single().Id, WorkTaskStatus.Failed, "Seed failure");
        var completed = kernel.CreateGoal("Completed goal");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == completed.Id.Value
                ? goal with { Status = GoalStatus.Completed }
                : goal).ToArray()
        });
        await repository.SaveAsync(kernel);

        // Fixed, distinct timestamps exercise DESC ordering without wall-clock pacing.
        await using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        await connection.OpenAsync();
        var index = 0;
        foreach (var goal in kernel.Goals.OrderBy(goal => goal.Objective, StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET updated_at = $updated WHERE id = $id";
            command.Parameters.AddWithValue("$updated", $"2026-01-0{++index}T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$id", goal.Id.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        return kernel;
    }
}
