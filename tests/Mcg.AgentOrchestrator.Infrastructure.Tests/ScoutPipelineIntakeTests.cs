using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ScoutPipelineIntakeTests
{
    [Xunit.Fact]
    public void AutomaticGoalIntakeRecordsScoutAndCreatesFourOrderedTasks()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs.");

        Xunit.Assert.Equal(
            [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            goal.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Intake pipeline decision (auto): scout", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ExplicitScoutChecksItsOwnRoles()
    {
        const string objective = "Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs.";
        var scoutPlan = GoalObjectivePlanner.Build(objective, GoalIntakePipeline.Scout);
        Xunit.Assert.Equal(
            [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            scoutPlan.TaskBoundaries.Select(boundary => boundary.Role));

        var withoutPlanner = AgentCatalog.Default().Agents
            .Where(agent => agent.Role != AgentRole.Planner)
            .ToArray();
        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            GoalLifecycleCommands.EnsureRequestedPipelineCanBeSatisfied(scoutPlan, withoutPlanner));
        Xunit.Assert.Contains("pipeline 'scout'", error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Planner", error.Message, StringComparison.Ordinal);
    }
}
