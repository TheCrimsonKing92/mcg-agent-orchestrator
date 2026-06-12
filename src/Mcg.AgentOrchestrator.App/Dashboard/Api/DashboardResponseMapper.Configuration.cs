using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static AgentDto ToAgentDto(AgentDefinition agent)
{
    return new AgentDto(
        agent.Id.Value,
        agent.Name,
        agent.Role,
        agent.Model.ProviderName,
        agent.Model.ModelName,
        agent.Model.ReasoningEffort,
        agent.Model.MaxOutputTokens,
        agent.Status,
        agent.ExecutionPolicy,
        agent.Subscription?.WorkerProfileName,
        agent.Subscription?.ModelAlias,
        agent.Subscription?.ReasoningEffort,
        agent.ComplexModel?.ProviderName,
        agent.ComplexModel?.ModelName,
        agent.ComplexModel?.MaxOutputTokens,
        agent.ComplexModel?.ReasoningEffort);
}

public static IReadOnlyList<WorkerProfileDto> ToWorkerProfileDtos(AgentCatalog agents, WorkerProfileCatalog catalog)
{
    var validations = OrchestratorHealthInspector
        .InspectCurrentEnvironment(agents, catalog)
        .WorkerProfiles
        .ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);

    return catalog.Profiles
        .Select(profile => ToWorkerProfileDto(profile, validations.GetValueOrDefault(profile.Name)))
        .ToList();
}

public static WorkerProfileDto ToWorkerProfileDto(WorkerProfile profile, WorkerProfileValidation? validation)
{
    return new WorkerProfileDto(
        profile.Name,
        profile.CommandTemplate,
        validation?.Executable ?? string.Empty,
        validation?.IsResolvable ?? false,
        validation?.IsEchoOnly ?? false,
        validation?.IsPatchCapable ?? false,
        validation?.IsOptional ?? false,
        validation?.Detail ?? "Worker profile was not inspected.");
}

public static SubscriptionPlanDto ToSubscriptionPlanDto(SubscriptionPlan plan)
{
    return new SubscriptionPlanDto(
        plan.GoalId,
        plan.Objective,
        plan.Status,
        plan.ReadyToPrepareCount,
        plan.ResolvableProfileCount,
        plan.RetryDeferredCount,
        plan.NextSubscriptionRetryAfter,
        plan.ReadyStartCostRisk,
        plan.ReadyStartPromptCharacterCount,
        plan.ReadyStartCostRiskDetails,
        plan.ReadyStartCostRecommendation,
        plan.ReadyModelUsage.Select(ToSubscriptionPlanModelSummaryDto).ToList(),
        plan.Items.Select(ToSubscriptionPlanItemDto).ToList());
}

private static SubscriptionPlanModelSummaryDto ToSubscriptionPlanModelSummaryDto(SubscriptionPlanModelSummary summary)
{
    return new SubscriptionPlanModelSummaryDto(
        summary.ProviderName,
        summary.ModelName,
        summary.ReadyCount,
        summary.TaskComplexity,
        summary.ReasoningEffort,
        summary.IsPotentiallyPaidProvider,
        summary.EstimatedPromptCharacterCount,
        summary.PreviousModelFitNoteCount,
        summary.PreviousAdequateCount,
        summary.PreviousOverkillCount,
        summary.PreviousUnderpoweredCount,
        summary.PreviousUnknownFitCount,
        summary.PreviousTaskShapes,
        summary.ModelFitRecommendation,
        summary.UsesComplexModel);
}

private static SubscriptionPlanItemDto ToSubscriptionPlanItemDto(SubscriptionPlanItem item)
{
    return new SubscriptionPlanItemDto(
        item.TaskNumber,
        item.TaskId,
        item.Role,
        item.TaskStatus,
        item.Description,
        item.AgentId,
        item.AgentName,
        item.ProviderName,
        item.ModelName,
        item.ExecutionPolicy,
        item.ProfileName,
        item.SubscriptionModelAlias,
        item.ProfileExists,
        item.ProfileIsResolvable,
        item.ProfileIsEchoOnly,
        item.ProfileIsPatchCapable,
        item.CanPrepare,
        item.Detail,
        item.RetryAfter,
        item.RetryDelaySeconds,
        item.TaskComplexity,
        item.SubscriptionModelName,
        item.SubscriptionReasoningEffort,
        item.EstimatedPromptCharacterCount,
        item.RecoverableSubscriptionLimitFailureCount,
        item.UsesComplexModel,
        item.CostGuardPromptCharacterCount);
}
}
