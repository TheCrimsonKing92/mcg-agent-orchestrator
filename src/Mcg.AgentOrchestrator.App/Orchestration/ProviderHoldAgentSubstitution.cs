using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Resolves a held assignment without mutating the authoritative kernel.</summary>
internal sealed class ProviderHoldAgentSubstitution(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight)
{
    internal abstract record Result
    {
        internal sealed record None(IReadOnlyList<string> Findings) : Result;
        internal sealed record WaitOnPin(IReadOnlyList<string> Findings) : Result;
        internal sealed record Substitute(AgentDefinition Agent, string HeldBindingKey, string EvidenceReceipt) : Result;
    }

    internal Result Resolve(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Assigned ||
            !agents.Any(agent => agent.Id == task.AssignedAgentId) ||
            goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderHoldSubstituted))
            return new Result.None([]);

        var assigned = preflight(goal, task, kernel.Goals);
        if (assigned.ErrorCode != WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode)
            return new Result.None(assigned.Findings);
        var hold = FindHold(assigned.Findings);
        if (hold is null) return new Result.None(assigned.Findings);
        if (IsPinned(goal, task.Id)) return new Result.WaitOnPin(assigned.Findings);

        // Reassignment exists only on this scratch view; every candidate uses the real preflight.
        var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        foreach (var candidate in agents)
        {
            if (candidate.Status != AgentStatus.Available || candidate.Role != task.RequiredRole ||
                candidate.Id == task.AssignedAgentId || !AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy))
                continue;
            var view = scratch.ReassignTaskAgent(goal.Id, task.Id, candidate);
            if (preflight(scratch.GetGoal(goal.Id), view, scratch.Goals).Allowed)
                return new Result.Substitute(candidate, hold.BindingKey, hold.EvidenceReceipt);
        }
        return new Result.None(assigned.Findings);
    }

    internal void ApplyBatch(Goal goal, IEnumerable<TaskId> taskIds)
    {
        foreach (var taskId in taskIds)
            if (Resolve(goal, kernel.GetTask(goal.Id, taskId)) is Result.Substitute substitute)
                kernel.SubstituteProviderHeldTaskAgent(goal.Id, taskId, substitute.Agent,
                    substitute.HeldBindingKey, substitute.EvidenceReceipt);
    }

    private ProviderBudgetExhaustionHold? FindHold(IReadOnlyList<string> findings)
    {
        var bindings = kernel.Goals.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        foreach (var binding in bindings)
            if (DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(kernel.Goals,
                    binding.Provider, binding.Credential, out var hold) &&
                findings.Any(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase) &&
                    finding.Contains($"binding {hold.BindingKey} (", StringComparison.OrdinalIgnoreCase)))
                return hold;
        return null;
    }

    private static bool IsPinned(Goal goal, TaskId taskId) => OfflineAssignedAgentRedelegation.IsOperatorPin(goal, taskId);
}
