using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalTaskDesyncCandidateTests : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task SqliteSelectionFindsProtectedTerminalGoalsWithoutWorktreeArtifacts()
    {
        var databasePath = Path.Combine(CreateTempDirectory(), "state.db");
        StateDbMigrations.EnsureUpToDate(databasePath);
        var repository = new SqliteOrchestratorStateRepository(databasePath);
        var kernel = new AgentOrchestratorKernel();
        var expected = new List<GoalId>();

        foreach (var status in new[] { GoalStatus.Superseded, GoalStatus.Cancelled, GoalStatus.Completed })
        {
            var task = new TaskSpec(TaskId.New(), "Stale work", AgentRole.Developer);
            var goal = kernel.CreateGoal($"Stale {status}", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel = WithGoalStatus(kernel, goal.Id, status);
            expected.Add(goal.Id);
        }

        var settledTask = new TaskSpec(TaskId.New(), "Settled work", AgentRole.Developer);
        var settled = kernel.CreateGoal("Settled", [settledTask]);
        kernel.ActivateGoal(settled.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(settled.Id, settledTask.Id, WorkTaskStatus.Cancelled, "settled");
        kernel = WithGoalStatus(kernel, settled.Id, GoalStatus.Completed);
        await repository.SaveAsync(kernel);

        var actual = await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync();
        Xunit.Assert.Equal(expected.OrderBy(id => id.Value), actual.OrderBy(id => id.Value));
    }
}
