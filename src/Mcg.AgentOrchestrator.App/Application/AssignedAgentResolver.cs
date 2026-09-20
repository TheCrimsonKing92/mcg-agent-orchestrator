using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Resolves the agent a task is assigned to. Dispatch preparation and goal advancement both need
/// this rule, so it lives beside them rather than being duplicated into either operation.
/// </summary>
internal static class AssignedAgentResolver
{
    internal static AgentDefinition Resolve(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        if (task.AssignedAgentId is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
        }

        return agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
    }
}
