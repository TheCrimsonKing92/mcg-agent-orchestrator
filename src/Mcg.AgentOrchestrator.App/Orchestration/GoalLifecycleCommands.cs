using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Dashboard.Api;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalLifecycleCommands
{
    public static Goal CreateAndActivateGoal(AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var plan = GoalObjectivePlanner.Build(objective, pipelineOverride: null, durationStats: kernel.BuildTaskDurationStats());
        return CreateAndActivateGoal(kernel, agents, plan);
    }

    public static Goal CreateAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        GoalObjectivePlan plan)
    {
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        EnsureRequestedPipelineCanBeSatisfied(plan, agents);
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
        IGoalLifecycleEventWriter? eventWriter = null,
        CollaborationItemRaise? collaborationItemRaise = null)
    {
        var plan = GoalObjectivePlanner.Build(objective, pipelineOverride: null, durationStats: kernel.BuildTaskDurationStats());
        return CreateAndActivateGoal(
            kernel,
            agents,
            plan,
            workspace,
            providers,
            eventWriter,
            collaborationItemRaise);
    }

    public static Goal CreateAndActivateGoal(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        GoalObjectivePlan plan,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IGoalLifecycleEventWriter? eventWriter = null,
        CollaborationItemRaise? collaborationItemRaise = null)
    {
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        EnsureRequestedPipelineCanBeSatisfied(plan, agents);
        var goal = CreateGoalFromPlan(kernel, plan);
        GoalRefinementGate.EnsureRefined(
            kernel,
            workspace,
            providers,
            goal,
            eventWriter: eventWriter,
            collaborationItemRaise: collaborationItemRaise);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static void EnsureRequestedPipelineCanBeSatisfied(
        GoalObjectivePlan plan,
        IReadOnlyList<AgentDefinition> agents)
    {
        if (!plan.PipelineDecision.IsOverride ||
            plan.PipelineDecision.Pipeline != GoalIntakePipeline.FiveRole)
        {
            return;
        }

        var missingRoles = plan.TaskBoundaries
            .Select(boundary => boundary.Role)
            .Distinct()
            .Where(role => !agents.Any(agent =>
                agent.Status == AgentStatus.Available &&
                agent.Role == role))
            .ToArray();
        if (missingRoles.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Requested intake pipeline 'five-role' cannot be satisfied; missing available agent role(s): {string.Join(", ", missingRoles)}. No goal was created.");
    }

    public static void EnsureRequestedPipelineMatchesPersistedGoal(
        GoalObjectivePlan requestedPlan,
        Goal persistedGoal)
    {
        if (!requestedPlan.PipelineDecision.IsOverride)
        {
            return;
        }

        var requestedRoles = requestedPlan.TaskBoundaries
            .Select(boundary => boundary.Role)
            .ToArray();
        var persistedRoles = persistedGoal.Tasks
            .Select(task => task.RequiredRole)
            .ToArray();
        var persistedSelectionSource = ResolvePersistedPipelineSelectionSource(persistedGoal);
        if (requestedRoles.SequenceEqual(persistedRoles) &&
            requestedPlan.PipelineDecision.SelectionSource.Equals(persistedSelectionSource, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Existing goal {persistedGoal.Id.Value[..8]} does not match the explicitly requested intake pipeline. " +
            $"Requested workflow='{requestedPlan.PipelineDecision.Workflow}', " +
            $"selectionSource='{requestedPlan.PipelineDecision.SelectionSource}', " +
            $"orderedRoles=[{FormatRoles(requestedRoles)}]; " +
            $"persisted workflow='{DescribePipeline(persistedRoles)}', " +
            $"selectionSource='{persistedSelectionSource}', " +
            $"orderedRoles=[{FormatRoles(persistedRoles)}]. Existing goal was not reused.");
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
        IGoalLifecycleEventWriter? eventWriter = null,
        CollaborationItemRaise? collaborationItemRaise = null)
    {
        var plan = GoalObjectivePlanner.Build(objective, GoalIntakePipeline.DeveloperOnly, kernel.BuildTaskDurationStats());
        GoalObjectivePlanner.ThrowIfBlocked(plan);
        var goal = CreateGoalFromPlan(kernel, plan);
        GoalRefinementGate.EnsureRefined(
            kernel,
            workspace,
            providers,
            goal,
            eventWriter: eventWriter,
            collaborationItemRaise: collaborationItemRaise);
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
        bool allowLargePaidSubscriptionStart = false,
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
            allowLargePaidSubscriptionStart,
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

    private static string ResolvePersistedPipelineSelectionSource(Goal goal)
    {
        var message = goal.Timeline
            .LastOrDefault(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.StartsWith("Intake pipeline decision (", StringComparison.Ordinal))
            ?.Message;
        if (message?.StartsWith("Intake pipeline decision (override):", StringComparison.Ordinal) == true)
        {
            return "explicitly-required";
        }

        if (message?.StartsWith("Intake pipeline decision (auto):", StringComparison.Ordinal) == true)
        {
            return "automatic";
        }

        return "unrecorded";
    }

    private static string DescribePipeline(IReadOnlyList<AgentRole> roles)
    {
        if (roles.SequenceEqual([AgentRole.Developer]))
        {
            return "developer-only";
        }

        if (roles.SequenceEqual([AgentRole.Developer, AgentRole.Reviewer]))
        {
            return "developer-reviewer";
        }

        if (roles.SequenceEqual(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer]))
        {
            return "five-role";
        }

        return "custom";
    }

    private static string FormatRoles(IEnumerable<AgentRole> roles) =>
        string.Join(", ", roles.Select(role => role.ToString()));

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


