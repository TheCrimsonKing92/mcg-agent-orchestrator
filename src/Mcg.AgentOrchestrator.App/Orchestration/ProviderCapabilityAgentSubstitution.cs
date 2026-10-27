using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Resolves a capability-blocked assignment without mutating the authoritative kernel.</summary>
internal sealed class ProviderCapabilityAgentSubstitution(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight)
{
    internal abstract record Result
    {
        internal sealed record None(IReadOnlyList<string> Findings) : Result;
        internal sealed record Substitute(AgentDefinition Agent, string CapabilityCode) : Result;
    }

    internal Result Resolve(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Assigned || !agents.Any(agent => agent.Id == task.AssignedAgentId))
            return new Result.None([]);

        var assigned = preflight(goal, task, kernel.ProviderHoldScope);
        if (assigned.ErrorCode != WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode ||
            goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderCapabilitySubstituted) ||
            OfflineAssignedAgentRedelegation.IsOperatorPin(goal, task.Id))
            return new Result.None(assigned.Findings);

        foreach (var candidate in agents)
        {
            if (candidate.Status != AgentStatus.Available || candidate.Role != task.RequiredRole ||
                candidate.Id == task.AssignedAgentId || !AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy))
                continue;

            // Each trial has its own timeline; rejected trial assignments never leak into later preflights.
            var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            scratch.ProviderHoldHistory.CopyFrom(kernel.ProviderHoldHistory);
            var view = scratch.ReassignTaskAgent(goal.Id, task.Id, candidate);
            if (preflight(scratch.GetGoal(goal.Id), view, scratch.ProviderHoldScope).Allowed)
                return new Result.Substitute(candidate, WorkerProfileDispatcher.ProviderCapabilityUnsupportedErrorCode);
        }
        return new Result.None(assigned.Findings);
    }

    internal void ApplyBatch(Goal goal, IEnumerable<TaskId> taskIds)
    {
        foreach (var taskId in taskIds)
            if (Resolve(kernel.GetGoal(goal.Id), kernel.GetTask(goal.Id, taskId)) is Result.Substitute substitute)
                kernel.SubstituteCapabilityBlockedTaskAgent(goal.Id, taskId, substitute.Agent, substitute.CapabilityCode);
    }
}
