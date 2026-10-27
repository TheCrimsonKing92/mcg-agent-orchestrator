using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Application;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Resolves a held assignment without mutating the authoritative kernel.</summary>
internal sealed class ProviderHoldAgentSubstitution(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight,
    Func<DateTimeOffset>? utcNow = null)
{
    internal abstract record Result
    {
        internal sealed record None(IReadOnlyList<string> Findings) : Result;
        internal sealed record WaitOnPin(IReadOnlyList<string> Findings) : Result;
        internal sealed record Substitute(AgentDefinition Agent, string HeldBindingKey, string EvidenceReceipt,
            ProviderSubscriptionCooldown? SubscriptionCooldown = null) : Result;
    }

    internal Result Resolve(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Assigned || task.LastProcess is { IsRunning: true } ||
            !agents.Any(agent => agent.Id == task.AssignedAgentId) ||
            goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderHoldSubstituted))
            return new Result.None([]);

        var assigned = preflight(goal, task, kernel.ProviderHoldScope);
        var hold = assigned.ErrorCode == WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode
            ? FindHold(assigned.Findings) : null;
        var agent = agents.First(candidate => candidate.Id == task.AssignedAgentId);
        var now = UtcNow;
        var cooldown = TryGetSubscriptionCooldown(goal, task, agent, now, out var observed)
            ? observed : null;
        if (assigned.Allowed || (hold is null && cooldown is null)) return new Result.None(assigned.Findings);
        if (IsPinned(goal, task.Id)) return new Result.WaitOnPin(assigned.Findings);

        // Reassignment exists only on this scratch view; every candidate uses the real preflight.
        foreach (var candidate in agents)
        {
            if (candidate.Status != AgentStatus.Available || candidate.Role != task.RequiredRole ||
                candidate.Id == task.AssignedAgentId || !AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy))
                continue;
            var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new SubstitutionClock(now));
            scratch.ProviderHoldHistory.CopyFrom(kernel.ProviderHoldHistory);
            var view = scratch.ReassignTaskAgent(goal.Id, task.Id, candidate);
            if (preflight(scratch.GetGoal(goal.Id), view, scratch.ProviderHoldScope).Allowed)
                return hold is not null
                    ? new Result.Substitute(candidate, hold.BindingKey, hold.EvidenceReceipt)
                    : new Result.Substitute(candidate, cooldown!.BindingKey, cooldown.EvidenceReceipt, cooldown);
        }
        return new Result.None(assigned.Findings);
    }

    internal void ApplyBatch(Goal goal, IEnumerable<TaskId> taskIds)
    {
        foreach (var taskId in taskIds)
        {
            goal = kernel.GetGoal(goal.Id);
            var task = kernel.GetTask(goal.Id, taskId);
            var revision = task.ConductorRoutingRevision;
            if (DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length > 0) continue;
            if (Resolve(goal, task) is Result.Substitute substitute &&
                ReferenceEquals(task, kernel.GetTask(goal.Id, taskId)) &&
                task.Status == WorkTaskStatus.Assigned && task.LastProcess is not { IsRunning: true } &&
                task.ConductorRoutingRevision == revision &&
                !IsPinned(goal, taskId) &&
                DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length == 0 &&
                (substitute.SubscriptionCooldown is null ||
                 (TryGetSubscriptionCooldown(goal, task, agents.First(agent => agent.Id == task.AssignedAgentId),
                     UtcNow, out var currentCooldown) &&
                  currentCooldown == substitute.SubscriptionCooldown)))
                kernel.SubstituteProviderHeldTaskAgent(goal.Id, taskId, substitute.Agent,
                    substitute.HeldBindingKey, substitute.EvidenceReceipt, substitute.SubscriptionCooldown);
        }
    }

    internal static bool CanAttemptSubscriptionSubstitution(Goal goal, TaskSpec task,
        IReadOnlyList<AgentDefinition> agents, DateTimeOffset now) =>
        task.Status == WorkTaskStatus.Assigned && task.LastProcess is not { IsRunning: true } &&
        !IsPinned(goal, task.Id) &&
        !goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderHoldSubstituted) &&
        DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length == 0 &&
        agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId) is { } assigned &&
        TryGetSubscriptionCooldown(goal, task, assigned, now, out _) &&
        agents.Any(candidate => candidate.Status == AgentStatus.Available &&
            candidate.Role == task.RequiredRole && candidate.Id != assigned.Id &&
            AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy) &&
            !candidate.Model.ProviderName.Equals(assigned.Model.ProviderName, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetSubscriptionCooldown(Goal goal, TaskSpec task, AgentDefinition assigned,
        DateTimeOffset now, out ProviderSubscriptionCooldown cooldown)
    {
        if (DispatchFailureClassifier.TryGetTaskSubscriptionCooldown(task, now, out cooldown) &&
            cooldown.ProviderName.Equals(assigned.Model.ProviderName, StringComparison.OrdinalIgnoreCase))
            return true;
        return DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(goal, task.Id,
            assigned.Model.ProviderName, now, out cooldown);
    }

    private ProviderBudgetExhaustionHold? FindHold(IReadOnlyList<string> findings)
    {
        var bindings = kernel.ProviderHoldScope.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        foreach (var binding in bindings)
            if (DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(kernel.ProviderHoldScope,
                    binding.Provider, binding.Credential, out var hold) &&
                findings.Any(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase) &&
                    finding.Contains($"binding {hold.BindingKey} (", StringComparison.OrdinalIgnoreCase)))
                return hold;
        return null;
    }

    private static bool IsPinned(Goal goal, TaskId taskId) => OfflineAssignedAgentRedelegation.IsOperatorPin(goal, taskId);

    private DateTimeOffset UtcNow => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;

    private sealed class SubstitutionClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
