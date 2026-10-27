using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Repairs unavailable ready-task assignments while preserving explicit operator pins.</summary>
internal static class OfflineAssignedAgentRedelegation
{
    internal const string Marker = "OFFLINE_AGENT_REDELEGATED";

    internal sealed record Result(IReadOnlySet<TaskId> ExcludedTaskIds, IReadOnlyList<ReadyBlockedDiagnostic> Blocked);

    internal static bool IsOperatorPin(Goal goal, TaskId taskId)
    {
        // Timeline position is authoritative even when the clock gives several events the same timestamp.
        var latestAssignment = goal.Timeline.LastOrDefault(evt => evt.TaskId == taskId &&
            evt.Kind is ProgressKind.TaskDelegated or ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
                ProgressKind.ProviderCapabilitySubstituted);
        return latestAssignment is { Kind: ProgressKind.TaskRedelegated } &&
            latestAssignment.Message.StartsWith("Reassigned ", StringComparison.Ordinal) &&
            !latestAssignment.Message.Contains("repaired stale", StringComparison.OrdinalIgnoreCase) &&
            !latestAssignment.Message.Contains(Marker, StringComparison.Ordinal) &&
            !latestAssignment.Message.Contains(ProviderSignInFailover.Marker, StringComparison.Ordinal);
    }

    internal static Result Apply(AgentOrchestratorKernel kernel, Goal goal,
        IReadOnlyList<AgentDefinition> agents, IEnumerable<TaskId> candidateIds)
    {
        var excluded = new HashSet<TaskId>();
        var blocked = new List<ReadyBlockedDiagnostic>();
        foreach (var taskId in candidateIds.Distinct())
        {
            var task = kernel.GetTask(goal.Id, taskId);
            if (task.Status != WorkTaskStatus.Assigned || task.AssignedAgentId is not { } assignedId)
                continue;
            var assigned = agents.FirstOrDefault(agent => agent.Id == assignedId);
            if (assigned?.Status == AgentStatus.Available)
                continue;
            var replacement = agents.FirstOrDefault(agent => agent.Status == AgentStatus.Available &&
                agent.Role == task.RequiredRole && AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy));
            if (replacement is null)
                continue;
            if (assigned is not null && IsOperatorPin(goal, task.Id))
            {
                excluded.Add(task.Id);
                blocked.Add(DispatchReadinessRules.BuildReadyBlockedDiagnostic(goal, task, agents,
                    "assigned-agent-unavailable-pinned", [$"agent={assignedId.Value} status={assigned.Status}"]));
                continue;
            }
            kernel.ReassignTaskAgent(goal.Id, task.Id, replacement,
                $"{Marker} from={assignedId.Value} from_status={assigned?.Status.ToString() ?? "absent"} to={replacement.Id.Value} role={task.RequiredRole}");
        }
        return new Result(excluded, blocked);
    }
}
