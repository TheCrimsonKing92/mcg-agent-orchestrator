using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each round trip owns a unique SQLite database and no process-wide state.
public sealed class ResearcherGoalSnapshotCompatibilityTests
{
    [Xunit.Fact]
    public async Task Snapshot_ResearcherGoal_RetainsRolesAndRequiresResearcherSeat()
    {
        var root = CreateTempDirectory();
        try
        {
            const string objective = "Implement src/Feature.cs with focused tests.";
            var kernel = new AgentOrchestratorKernel();
            var goal = ResearcherGoalPlanFixture.Create(kernel, objective);
            var database = Path.Combine(root, "state.db");
            var repository = CreateMigratedStateRepository(database);
            await repository.SaveAsync(kernel);
            var loaded = await CreateMigratedStateRepository(database).LoadAsync();
            var loadedGoal = Xunit.Assert.Single(loaded.Goals);
            Xunit.Assert.Equal(goal.Id, loadedGoal.Id);
            Xunit.Assert.Equal(
                [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
                loadedGoal.Tasks.Select(task => task.RequiredRole));

            var agents = AgentCatalog.Default().Agents;
            loaded.ActivateGoal(loadedGoal.Id, agents);
            var researcher = Xunit.Assert.Single(loadedGoal.Tasks, task => task.RequiredRole == AgentRole.Researcher);
            Xunit.Assert.NotNull(researcher.AssignedAgentId);
            var assigned = Xunit.Assert.Single(agents, agent => agent.Id == researcher.AssignedAgentId);
            Xunit.Assert.Equal(AgentRole.Researcher, assigned.Role);
            Xunit.Assert.Equal(AgentStatus.Available, assigned.Status);
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, researcher.Status);

            var scout = GoalObjectivePlanner.Build(objective, GoalIntakePipeline.Scout);
            var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
                GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(scout, loadedGoal));
            Xunit.Assert.Contains("persisted workflow='five-role'", error.Message, StringComparison.Ordinal);

            var controlKernel = await CreateMigratedStateRepository(database).LoadAsync();
            var controlGoal = Xunit.Assert.Single(controlKernel.Goals);
            var withoutResearcher = agents.Where(agent => agent.Role != AgentRole.Researcher).ToArray();
            controlKernel.ActivateGoal(controlGoal.Id, withoutResearcher);
            var unassigned = Xunit.Assert.Single(controlGoal.Tasks, task => task.RequiredRole == AgentRole.Researcher);
            Xunit.Assert.Null(unassigned.AssignedAgentId);
            Xunit.Assert.Equal(WorkTaskStatus.Pending, unassigned.Status);
            var assignedTasks = controlGoal.Tasks.Where(task => task.AssignedAgentId is not null).ToArray();
            Xunit.Assert.NotEmpty(assignedTasks);
            Xunit.Assert.All(assignedTasks, task => Xunit.Assert.Equal(task.RequiredRole,
                Xunit.Assert.Single(withoutResearcher, agent => agent.Id == task.AssignedAgentId).Role));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
