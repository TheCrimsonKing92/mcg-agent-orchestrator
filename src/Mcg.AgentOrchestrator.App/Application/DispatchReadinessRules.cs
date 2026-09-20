using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Stateless readiness, ordering and parallel-plan rules for goal dispatch. Nothing here reads a
/// process probe or a clock the caller can replace, so these stay static and callers such as
/// <c>ConductorDriver</c> need no operation instance.
/// </summary>
public static class DispatchReadinessRules
{
    internal static bool HasAssignedDispatchCandidates(Goal goal) =>
        goal.Tasks.Any(IsSubscriptionStartCandidate);

    internal static bool IsSubscriptionStartCandidate(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Assigned &&
            !HasBlockingRunningProcess(task) &&
            !WorkerProfileDispatcher.IsTaskRetryDeferred(task, DateTimeOffset.UtcNow, out _);
    }

    private static bool HasBlockingRunningProcess(TaskSpec task)
    {
        return task.LastProcess is { IsRunning: true };
    }

    internal static bool IsRefinementEligible(Goal goal, TaskSpec task) =>
        !GoalRefinementWorkCoordinator.HasPendingWork(goal) ||
        task.RequiredRole == AgentRole.Researcher;

    public static ParallelExecutionPlan BuildReadyTaskParallelPlan(
        Goal goal,
        IReadOnlyList<AgentDefinition>? agents = null,
        bool approveHighRiskOwnership = false)
    {
        var assigned = goal.Tasks
            .Where(IsSubscriptionStartCandidate)
            .Where(task => IsRefinementEligible(goal, task))
            .ToList();
        var intents = assigned
            .Select(task =>
            {
                var scope = GoalFileScopeInference.ForScheduling(goal, task);
                return new ParallelExecutionIntent(
                    task.Id.Value,
                    goal.Id.Value,
                    scope.Includes,
                    ProviderKey: ResolveParallelProviderKey(task, agents),
                    DependsOn: BuildIncompleteEarlierStageDependencies(goal, task),
                    ScopeConfidence: scope.Confidence);
            })
            .ToList();
        var providerQuotas = intents
            .Where(intent => !string.IsNullOrWhiteSpace(intent.ProviderKey))
            .Select(intent => intent.ProviderKey!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(provider => new ParallelExecutionProviderQuota(provider, 1))
            .ToList();
        return ParallelExecutionPlanner.Build(intents, providerQuotas, approveHighRiskOwnership);
    }

    internal static ParallelSafeBatchSelection SelectFirstParallelSafeAssignedBatch(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        bool approveHighRiskOwnership = false)
    {
        var assigned = goal.Tasks
            .Where(IsSubscriptionStartCandidate)
            .Where(task => IsRefinementEligible(goal, task))
            .ToList();
        var plan = BuildReadyTaskParallelPlan(goal, agents, approveHighRiskOwnership);
        var firstBatch = plan.Batches.FirstOrDefault();
        var taskIds = firstBatch is null
            ? []
            : assigned
                .Where(task => firstBatch.IntentIds.Contains(task.Id.Value, StringComparer.OrdinalIgnoreCase))
                .Select(task => task.Id)
                .ToHashSet();
        return new ParallelSafeBatchSelection(
            taskIds,
            plan,
            BuildAssignedTaskExclusionDiagnostics(goal, agents, plan, taskIds));
    }

    private static IReadOnlyList<ReadyBlockedDiagnostic> BuildAssignedTaskExclusionDiagnostics(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        ParallelExecutionPlan plan,
        IReadOnlySet<TaskId> selectedTaskIds)
    {
        var diagnostics = new List<ReadyBlockedDiagnostic>();
        var decisionsByTaskId = plan.Decisions.ToDictionary(decision => decision.IntentId, StringComparer.OrdinalIgnoreCase);

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            if (selectedTaskIds.Contains(task.Id))
            {
                continue;
            }

            if (task.LastProcess is { IsRunning: true } process)
            {
                diagnostics.Add(BuildReadyBlockedDiagnostic(
                    goal,
                    task,
                    agents,
                    "last-process-running",
                    [$"LastProcess.IsRunning is true for pid {process.ProcessId}; exit artifact path {process.ExitCodePath}"]));
                continue;
            }

            if (decisionsByTaskId.TryGetValue(task.Id.Value, out var decision))
            {
                if (decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval &&
                    decision.Reasons.Any(reason => reason.Contains("operator approval", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                diagnostics.Add(BuildReadyBlockedDiagnostic(
                    goal,
                    task,
                    agents,
                    decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval
                        ? "unmet-dependency"
                        : "parallel-serialized",
                    decision.Reasons));
                continue;
            }

            var predecessor = goal.Tasks.FirstOrDefault(candidate =>
                IsEarlierSdlcStage(candidate.RequiredRole, task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed);
            if (predecessor is not null)
            {
                diagnostics.Add(BuildReadyBlockedDiagnostic(
                    goal,
                    task,
                    agents,
                    "unmet-dependency",
                    [$"predecessor {predecessor.Id.Value} is {predecessor.Status}, not Completed"]));
            }
        }

        return diagnostics;
    }

    internal static ReadyBlockedDiagnostic BuildReadyBlockedDiagnostic(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        string reason,
        IReadOnlyList<string>? details = null)
    {
        return new ReadyBlockedDiagnostic(
            goal.Id.Value[..8],
            TaskDisplayNumber.Resolve(goal, task.Id),
            task.Id.Value,
            ResolveReadyBlockedProvider(task, agents),
            reason,
            details);
    }

    private static string ResolveReadyBlockedProvider(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        if (task.AssignedAgentId is null)
        {
            return "unknown";
        }

        var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        return agent?.Subscription?.WorkerProfileName ?? agent?.Model.ProviderName ?? "unknown";
    }

    private static string[] BuildIncompleteEarlierStageDependencies(Goal goal, TaskSpec task)
    {
        return goal.Tasks
            .Where(candidate => IsEarlierSdlcStage(candidate.RequiredRole, task.RequiredRole))
            .Where(candidate => candidate.Status != WorkTaskStatus.Completed)
            .Select(candidate => candidate.Id.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsEarlierSdlcStage(AgentRole candidate, AgentRole current)
    {
        return SdlcStageOrder(candidate) is { } candidateOrder &&
            SdlcStageOrder(current) is { } currentOrder &&
            candidateOrder < currentOrder;
    }

    internal static bool IsEarlierSdlcStageOf(AgentRole candidate, AgentRole current) =>
        IsEarlierSdlcStage(candidate, current);

    private static int? SdlcStageOrder(AgentRole role)
    {
        return role switch
        {
            AgentRole.Researcher => 0,
            AgentRole.Planner => 1,
            AgentRole.Developer => 2,
            AgentRole.Tester => 3,
            AgentRole.Reviewer => 4,
            _ => null
        };
    }

    private static string? ResolveParallelProviderKey(TaskSpec task, IReadOnlyList<AgentDefinition>? agents)
    {
        if (task.AssignedAgentId is null || agents is null)
        {
            return null;
        }

        var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        return agent?.Model.ProviderName;
    }
}

internal sealed record ParallelSafeBatchSelection(
    HashSet<TaskId> TaskIds,
    ParallelExecutionPlan Plan,
    IReadOnlyList<ReadyBlockedDiagnostic> Blocked);
