using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Dashboard.Api;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLifecycleCommands
{
    public static Goal CreateAndActivateGoal(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var plan = GoalObjectivePlanner.Build(objective, pipelineOverride: null, durationStats: kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        string objective,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IGoalLifecycleEventWriter? eventWriter = null)
    {
        var plan = GoalObjectivePlanner.Build(objective, pipelineOverride: null, durationStats: kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan);
        GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal, eventWriter: eventWriter);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateSimpleGoal(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var plan = GoalObjectivePlanner.Build(objective, GoalIntakePipeline.DeveloperOnly, kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateAndActivateSimpleGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        string objective,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IGoalLifecycleEventWriter? eventWriter = null)
    {
        var plan = GoalObjectivePlanner.Build(objective, GoalIntakePipeline.DeveloperOnly, kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan);
        GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal, eventWriter: eventWriter);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static Goal CreateDormantGoal(
        AgentOrchestratorKernel kernel,
        string objective,
        GoalIntakePipeline pipeline,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IGoalLifecycleEventWriter? eventWriter = null,
        GoalId? sliceBatchParentId = null)
    {
        var plan = GoalObjectivePlanner.Build(objective, pipeline, kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan, sliceBatchParentId);
        GoalRefinementGate.EnsureRefined(kernel, workspace, providers, goal, eventWriter: eventWriter);
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

    private static Goal CreateGoalFromPlan(
        AgentOrchestratorKernel kernel,
        GoalObjectivePlan plan,
        GoalId? sliceBatchParentId = null)
    {
        var goal = kernel.CreateGoal(
            plan.Objective,
            plan.TaskBoundaries
                .Select(boundary => new TaskSpec(
                    TaskId.New(),
                    boundary.Purpose,
                    boundary.Role,
                    boundary.Verification))
                .ToList(),
            sliceBatchParentId);
        kernel.RecordGoalPolicyDecision(goal.Id, BuildPipelineDecisionMessage(plan));
        RecordCapabilityWarnings(kernel, goal.Id, plan.CapabilityWarnings);
        return goal;
    }

    private static string BuildPipelineDecisionMessage(GoalObjectivePlan plan)
    {
        var source = plan.PipelineDecision.IsOverride ? "override" : "auto";
        return $"Intake pipeline decision ({source}): {plan.PipelineDecision.Workflow}; reasons: {string.Join("; ", plan.PipelineDecision.Reasons)}; risk labels: {string.Join(", ", plan.RiskLabels)}.";
    }

    internal static void RecordCapabilityWarnings(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            kernel.RecordGoalPolicyDecision(goalId, warning);
        }
    }
}


