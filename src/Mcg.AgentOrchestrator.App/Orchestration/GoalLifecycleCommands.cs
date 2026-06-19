using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Dashboard.Api;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLifecycleCommands
{
    public static Goal CreateAndActivateGoal(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var goal = kernel.CreateGoal(objective);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        string objective,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers)
    {
        var goal = kernel.CreateGoal(objective);
        GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateSimpleGoal(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var goal = kernel.CreateGoal(
            objective,
            [
                new TaskSpec(
                    TaskId.New(),
                    objective,
                    AgentRole.Developer,
                    "Record concrete evidence that the objective is complete. Use an automated command when practical, or record manual verification evidence.")
            ]);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateSimpleGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        string objective,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers)
    {
        var goal = kernel.CreateGoal(
            objective,
            [
                new TaskSpec(
                    TaskId.New(),
                    objective,
                    AgentRole.Developer,
                    "Record concrete evidence that the objective is complete. Use an automated command when practical, or record manual verification evidence.")
            ]);
        GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateActivateAndHandoffGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace,
        string objective,
        bool simple,
        IModelProviderRegistry? providers = null)
    {
        var goal = simple
            ? CreateAndActivateSimpleGoal(
                kernel,
                agents,
                objective,
                workspace,
                providers ?? new InMemoryModelProviderRegistry([]))
            : CreateAndActivateGoal(
                kernel,
                agents,
                objective,
                workspace,
                providers ?? new InMemoryModelProviderRegistry([]));

        GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
            kernel,
            agents,
            profiles,
            workspace,
            goal,
            providers: providers);

        return goal;
    }
}


