using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record CrossGoalSubscriptionStartPlan(
    IReadOnlyList<CrossGoalSubscriptionStartCandidate> Candidates,
    ParallelExecutionPlan ParallelPlan)
{
    public IReadOnlyList<CrossGoalSubscriptionStartCandidate> FirstBatchCandidates =>
        ParallelPlan.Batches.FirstOrDefault() is not { } firstBatch
            ? []
            : Candidates
                .Where(candidate => firstBatch.IntentIds.Contains(candidate.GoalId, StringComparer.OrdinalIgnoreCase))
                .ToList();
}

public sealed record CrossGoalSubscriptionStartCandidate(
    string GoalId,
    string GoalPrefix,
    string Objective,
    IReadOnlyList<int> TaskNumbers,
    IReadOnlyList<string> TaskIds,
    IReadOnlyList<string> TargetPaths,
    IReadOnlyList<string> RequiredResources,
    IReadOnlyList<string> DependsOn,
    string? ProviderKey,
    bool RequiresCostConfirmation,
    string Detail,
    RepositoryScopeConfidence ScopeConfidence);

public static class CrossGoalSubscriptionStartPlanner
{
    public static CrossGoalSubscriptionStartPlan Build(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        bool costRiskConfirmed = false)
    {
        var candidates = kernel.Goals
            .Where(goal => goal.Status is GoalStatus.Active or GoalStatus.WaitingForHuman)
            .Select(goal => BuildCandidate(goal, agents, profiles, kernel.Goals))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToList();
        var intents = candidates.Select(candidate => new ParallelExecutionIntent(
            candidate.GoalId,
            GoalKey: candidate.GoalId,
            TargetPaths: candidate.TargetPaths,
            RequiredResources: candidate.RequiredResources,
            RequiresOperatorApproval: candidate.RequiresCostConfirmation && !costRiskConfirmed,
            ProviderKey: candidate.ProviderKey,
            DependsOn: candidate.DependsOn,
            ScopeConfidence: candidate.ScopeConfidence)).ToList();
        var providerQuotas = intents
            .Where(intent => !string.IsNullOrWhiteSpace(intent.ProviderKey))
            .Select(intent => intent.ProviderKey!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(provider => new ParallelExecutionProviderQuota(provider, 1))
            .ToList();
        return new CrossGoalSubscriptionStartPlan(
            candidates,
            ParallelExecutionPlanner.Build(
                intents,
                providerQuotas,
                alreadySatisfiedDependencies: kernel.KnownCompletedDependencyGoals.Select(id => id.Value).ToArray()));
    }

    private static CrossGoalSubscriptionStartCandidate? BuildCandidate(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IReadOnlyCollection<Goal> providerHoldScope)
    {
        var subscriptionPlan = SubscriptionPlanBuilder.Build(goal, agents, profiles, providerHoldScope: providerHoldScope);
        var readinessVerdict = DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, subscriptionPlan, DateTimeOffset.UtcNow);
        if (readinessVerdict is not DispatchReadinessReady)
        {
            return null;
        }

        var readyItems = subscriptionPlan.Items
            .Where(item => item is { CanPrepare: true, TaskStatus: WorkTaskStatus.Assigned })
            .ToList();
        if (readyItems.Count == 0)
        {
            return null;
        }

        var taskIds = readyItems.Select(item => item.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readyTasks = goal.Tasks
            .Where(task => taskIds.Contains(task.Id.Value))
            .ToList();
        var scopes = readyTasks
            .Select(task => GoalFileScopeInference.ForScheduling(goal, task))
            .ToArray();
        var targetPaths = scopes
            .SelectMany(scope => scope.Includes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scopeConfidence = scopes.Any(scope => scope.Confidence == RepositoryScopeConfidence.Unknown)
            ? RepositoryScopeConfidence.Unknown
            : RepositoryScopeConfidence.Precise;
        var resources = new List<string> { $"goal-state:{goal.Id.Value}" };
        if (readyTasks.Any(task => task.RequiredRole is AgentRole.Developer or AgentRole.Tester))
        {
            resources.Add($"build-env:{goal.Id.Value[..8]}");
        }

        var providerKeys = readyItems
            .Select(item => item.ProviderName)
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var providerKey = providerKeys.Count == 1 ? providerKeys[0] : providerKeys.Count > 1 ? "mixed" : null;
        var detail = subscriptionPlan.ReadyStartCostRisk is null
            ? $"{readyItems.Count} ready subscription task(s)"
            : $"{readyItems.Count} ready subscription task(s); cost risk={subscriptionPlan.ReadyStartCostRisk}";

        return new CrossGoalSubscriptionStartCandidate(
            goal.Id.Value,
            goal.Id.Value[..8],
            goal.Objective,
            readyItems.Select(item => item.TaskNumber).ToList(),
            readyItems.Select(item => item.TaskId).ToList(),
            targetPaths,
            resources,
            goal.DependsOn.Select(id => id.Value).ToArray(),
            providerKey,
            subscriptionPlan.ReadyStartCostRisk is not null,
            detail,
            scopeConfidence);
    }
}
