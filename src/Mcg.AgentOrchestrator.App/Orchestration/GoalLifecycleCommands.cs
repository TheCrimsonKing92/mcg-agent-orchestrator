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

    public static Goal CreateActivateAndHandoffGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace,
        string objective,
        bool simple)
    {
        var goal = simple
            ? CreateAndActivateSimpleGoal(kernel, agents, objective)
            : CreateAndActivateGoal(kernel, agents, objective);

        GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
            kernel,
            agents,
            profiles,
            workspace,
            goal);

        return goal;
    }
}


