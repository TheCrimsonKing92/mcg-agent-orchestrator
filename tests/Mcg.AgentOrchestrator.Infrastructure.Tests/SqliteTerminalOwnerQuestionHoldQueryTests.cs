using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: this test owns its database, connections and temporary root.
public sealed class SqliteTerminalOwnerQuestionHoldQueryTests
{
    [Fact]
    public async Task TerminalStewardHolds_ProjectOnlyNeededFields_WithoutDeserializingGoals()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var statuses = new[] { GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Superseded,
                GoalStatus.Active, GoalStatus.Failed, GoalStatus.Parked, GoalStatus.Verified };
            var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-6));
            var kernel = new AgentOrchestratorKernel();
            foreach (var status in statuses) kernel.CreateGoal(status.ToString());
            var snapshot = kernel.ExportSnapshot();
            var goals = snapshot.Goals.Select((goal, index) => goal with
            {
                Status = statuses[index], CurrentHold = new GoalHoldSnapshot("hold-" + index,
                    "STEWARD-OWNER-QUESTION", "question=Review? evidence=[receipt]", started)
            }).ToArray();
            await new SqliteOrchestratorStateRepository(db).SaveAsync(
                AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = goals }));
            await using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await connection.OpenAsync();
                foreach (var goal in goals)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
                    command.Parameters.AddWithValue("$id", goal.Id);
                    // Valid JSON that cannot be deserialized as a GoalSnapshot proves SQL projection.
                    command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new
                    {
                        Tasks = "not-a-task-array", CurrentHold = goal.CurrentHold
                    }));
                    Assert.Equal(1, await command.ExecuteNonQueryAsync());
                }
            }

            IOrchestratorStateQueries queries = SqliteOrchestratorStateRepository.OpenReadOnly(db);
            var actual = await queries.ListTerminalOwnerQuestionHoldsAsync();

            Assert.Equal(goals.Take(3).Select(goal => new TerminalOwnerQuestionHold(goal.Id,
                    goal.CurrentHold!.Identity, goal.CurrentHold.State, goal.CurrentHold.Blocker, started))
                .OrderBy(hold => hold.GoalId), actual.OrderBy(hold => hold.GoalId));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task AbsentAndOtherHolds_AreExcluded()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal("No hold");
            kernel.CreateGoal("Other hold");
            var snapshot = kernel.ExportSnapshot();
            await new SqliteOrchestratorStateRepository(db).SaveAsync(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select((goal, index) => goal with
                {
                    Status = GoalStatus.Completed, CurrentHold = index == 0 ? null :
                        new GoalHoldSnapshot("other", "acceptance", "blocked", DateTimeOffset.MinValue)
                }).ToArray()
            }));

            Assert.Empty(await SqliteOrchestratorStateRepository.OpenReadOnly(db).ListTerminalOwnerQuestionHoldsAsync());
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
