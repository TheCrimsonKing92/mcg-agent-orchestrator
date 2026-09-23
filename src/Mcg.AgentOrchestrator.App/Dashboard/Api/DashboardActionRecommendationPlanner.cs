using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal enum DashboardActionRecommendationSource
{
    NextAction,
    FailureTriage,
    AcceptanceQueue,
    Recovery,
    Capacity,
    AutonomyPolicy,
    GoalHealth
}

internal sealed record DashboardActionRecommendationReport(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    DashboardActionRecommendation? Primary,
    IReadOnlyList<DashboardActionRecommendation> Secondary,
    IReadOnlyList<string> SourceSummaries);

internal sealed record DashboardActionRecommendation(
    DashboardActionRecommendationSource Source,
    string Title,
    string Reason,
    string SuggestedCommand,
    string? ApiMethod,
    string? ApiPath,
    bool CanApply,
    bool RequiresOperatorGate);

internal static class DashboardActionRecommendationPlanner
{
    public static DashboardActionRecommendationReport Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        string executionDirectory,
        AutonomyPolicy policy)
    {
        var recommendations = new List<DashboardActionRecommendation>();
        var sourceSummaries = new List<string>();
        var health = GoalHealthEvaluator.Build(kernel, goal, agents, workerProfiles, executionDirectory, policy);
        sourceSummaries.Add($"goal health: {health.Disposition} score={health.Score} - {health.Recommendation}");
        recommendations.Add(BuildHealth(health));

        var nextActions = kernel.BuildNextActions(goal.Id);
        var nextAction = nextActions.Items.FirstOrDefault();
        if (nextAction is not null)
        {
            recommendations.Add(BuildNextAction(goal, nextAction, agents, workerProfiles, policy));
        }

        sourceSummaries.Add($"next actions: {nextActions.Items.Count}");

        var triage = FailureTriagePlanner.Build(kernel, goal, agents, executionDirectory, policy);
        sourceSummaries.Add($"failure triage: {triage.Items.Count}");
        recommendations.AddRange(triage.Items.Take(2).Select(BuildFailureTriage));

        var recovery = GoalRecoveryPlanner.Build(kernel, goal, executionDirectory);
        sourceSummaries.Add($"recovery: {recovery.RecommendedActions.Count} action(s)");
        recommendations.AddRange(recovery.RecommendedActions.Take(2).Select(BuildRecovery));

        var acceptance = AcceptanceQueuePlanner.Build(kernel, executionDirectory, policy)
            .Items
            .FirstOrDefault(item => item.GoalId == goal.Id);
        if (acceptance is not null)
        {
            sourceSummaries.Add($"acceptance queue: {acceptance.Disposition}");
            recommendations.Add(BuildAcceptanceQueue(acceptance));
        }
        else
        {
            sourceSummaries.Add("acceptance queue: no item");
        }

        var subscription = SubscriptionPlanBuilder.Build(
            goal,
            agents,
            workerProfiles,
            task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, agents),
            providerHoldScope: kernel.Goals);
        sourceSummaries.Add($"capacity: {subscription.CapacitySchedule.Disposition} ready={subscription.CapacitySchedule.ReadyNowCount} deferred={subscription.CapacitySchedule.DeferredCount}");
        if (subscription.CapacitySchedule.Disposition != ProviderCapacityDisposition.Ready ||
            subscription.CapacitySchedule.HasCostRisk)
        {
            recommendations.Add(BuildCapacity(subscription.CapacitySchedule, goal.Id.Value[..8]));
        }

        var primary = SelectPrimary(recommendations);
        var secondary = recommendations
            .Where(item => !ReferenceEquals(item, primary))
            .Take(5)
            .ToArray();
        return new DashboardActionRecommendationReport(
            goal.Id.Value,
            goal.Id.Value[..8],
            policy.Name,
            primary,
            secondary,
            sourceSummaries);
    }

    private static DashboardActionRecommendation BuildNextAction(
        Goal goal,
        NextActionItem item,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        AutonomyPolicy policy)
    {
        var automation = NextActionAutomationPolicy.Build(item);
        var policyAction = MapAutomationAction(automation.Kind);
        var policyAllows = policyAction is null || policy.Allows(policyAction.Value);
        var control = DashboardNextActionControls.Build(goal, item, workerProfiles, agentDefinitions: agents);
        var command = ConsoleViews.BuildSuggestedCommand(goal, item, agents);
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.NextAction,
            $"Current next action: {item.Kind}",
            policyAllows
                ? item.Message
                : $"Policy '{policy.Name}' blocks automatic {automation.Kind}; use supervised-auto or run manually after review.",
            command,
            control?.Method,
            control?.Url,
            automation.CanExecute && policyAllows,
            !automation.CanExecute || !policyAllows);
    }

    private static DashboardActionRecommendation BuildFailureTriage(FailureTriageItem item)
    {
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.FailureTriage,
            $"Failure triage: {item.Cause}",
            item.Explanation,
            item.SuggestedCommand,
            null,
            null,
            item.CanAutoApply,
            item.RequiresOperatorGate);
    }

    private static DashboardActionRecommendation BuildRecovery(string command)
    {
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.Recovery,
            "Recovery recommendation",
            "Goal recovery found an interrupted or inconsistent state to inspect before continuing.",
            command,
            null,
            null,
            CanApply: false,
            RequiresOperatorGate: true);
    }

    private static DashboardActionRecommendation BuildAcceptanceQueue(AcceptanceQueueItem item)
    {
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.AcceptanceQueue,
            $"Acceptance queue: {item.Disposition}",
            item.Reason,
            item.SuggestedCommand,
            null,
            null,
            item.Disposition == AcceptanceQueueDisposition.Ready && item.AcceptanceAllowed && item.CleanupAllowed,
            item.Disposition != AcceptanceQueueDisposition.Ready || !item.AcceptanceAllowed || !item.CleanupAllowed);
    }

    private static DashboardActionRecommendation BuildCapacity(ProviderCapacitySchedule schedule, string goalPrefix)
    {
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.Capacity,
            $"Provider capacity: {schedule.Disposition}",
            schedule.Recommendation,
            $"subscription-plan {goalPrefix}",
            "GET",
            $"/api/goals/{goalPrefix}/subscription-plan",
            CanApply: false,
            RequiresOperatorGate: schedule.Disposition != ProviderCapacityDisposition.Ready || schedule.HasCostRisk);
    }

    private static DashboardActionRecommendation BuildHealth(GoalHealthReport health)
    {
        return new DashboardActionRecommendation(
            DashboardActionRecommendationSource.GoalHealth,
            $"Goal health: {health.Disposition} ({health.Score})",
            health.Recommendation,
            health.SuggestedCommand,
            null,
            null,
            CanApply: false,
            RequiresOperatorGate: health.Disposition is GoalHealthDisposition.Blocked or GoalHealthDisposition.NeedsOperator or GoalHealthDisposition.ProviderLimited);
    }

    private static DashboardActionRecommendation? SelectPrimary(IReadOnlyList<DashboardActionRecommendation> recommendations)
    {
        return recommendations.FirstOrDefault(item => item.Source == DashboardActionRecommendationSource.NextAction) ??
            recommendations.FirstOrDefault(item => item.CanApply) ??
            recommendations.FirstOrDefault();
    }

    private static AutonomyAction? MapAutomationAction(NextActionAutomationKind kind)
    {
        return kind switch
        {
            NextActionAutomationKind.RunAssignedTask => AutonomyAction.ModelRun,
            NextActionAutomationKind.StartRecordedDispatch => AutonomyAction.DispatchStart,
            NextActionAutomationKind.RefreshRunningProcess => AutonomyAction.Refresh,
            NextActionAutomationKind.DelegatePendingTask => AutonomyAction.DispatchStart,
            _ => null
        };
    }
}
