using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
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

public static SubscriptionPlanDto BuildSubscriptionPlan(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    Func<TaskSpec, int?>? estimatePromptCharacterCount = null)
{
    var validations = OrchestratorHealthInspector
        .InspectCurrentEnvironment(new AgentCatalog(agents), profiles)
        .WorkerProfiles
        .ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);

    var items = goal.Tasks
        .Select(task => BuildSubscriptionPlanItem(goal, task, agents, profiles, validations, estimatePromptCharacterCount))
        .ToList();
    var readyModelUsage = BuildSubscriptionPlanModelSummary(goal, items);
    var readyStartRisk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(items, readyModelUsage);

    return new SubscriptionPlanDto(
        goal.Id.Value,
        OutputTextPreview.CreateSummary(goal.Objective).Text,
        goal.Status,
        items.Count(item => item.CanPrepare),
        items.Count(item => item.ProfileName is not null && item.ProfileIsResolvable && !item.ProfileIsEchoOnly && item.ProfileIsPatchCapable),
        items.Count(item => item.RetryDelaySeconds is > 0),
        items
            .Where(item => item.RetryDelaySeconds is > 0)
            .Select(item => item.RetryAfter)
            .Where(item => item is not null)
            .OrderBy(item => item)
            .FirstOrDefault(),
        readyStartRisk is null ? null : SubscriptionPromptCostGuard.BuildInlineLabel(readyStartRisk),
        readyStartRisk?.PromptCharacterCount,
        readyStartRisk?.Details ?? [],
        readyStartRisk is null ? null : SubscriptionPromptCostGuard.BuildRecommendation(readyStartRisk),
        readyModelUsage,
        items);
}

public static SubscriptionPlanItemDto BuildSubscriptionPlanItem(
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IReadOnlyDictionary<string, WorkerProfileValidation> validations,
    Func<TaskSpec, int?>? estimatePromptCharacterCount = null)
{
    var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, task.Id);
    if (task.AssignedAgentId is null)
    {
        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            OutputTextPreview.CreateSummary(task.Description).Text,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            "Task is not assigned to an agent.");
    }

    var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
    if (agent is null)
    {
        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            OutputTextPreview.CreateSummary(task.Description).Text,
            task.AssignedAgentId.Value,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            $"Assigned agent '{task.AssignedAgentId.Value}' was not found in the agent catalog.");
    }

    var templateVariables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(agent, goal, task);
    var effectiveProviderName = GetTemplateValue(templateVariables, "providerName") ?? agent.Model.ProviderName;
    var effectiveModelName = GetTemplateValue(templateVariables, "apiModelName") ?? agent.Model.ModelName;
    var subscriptionModelName = GetTemplateValue(templateVariables, "subscriptionModelName");
    var subscriptionReasoningEffort = GetTemplateValue(templateVariables, "subscriptionReasoningEffort");
    var taskComplexity = TryParseTaskComplexity(GetTemplateValue(templateVariables, "taskComplexity"));

    try
    {
        var profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task);
        var profile = profiles.Profiles.FirstOrDefault(candidate => candidate.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        validations.TryGetValue(profileName, out var validation);
        var hasProfile = profile is not null;
        var isEchoOnly = profile is not null && WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate);
        var pinsSelectedModel = profile is not null &&
            WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate);
        var pinsSelectedReasoning = !RequiresSubscriptionReasoningPlaceholder(effectiveProviderName, subscriptionReasoningEffort) ||
            (profile is not null && WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate));
        var patchCapability = profile is null
            ? new WorkerProfilePatchCapability(false, "Worker profile was not found.")
            : WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
        var requiresPatchCapability = task.RequiredRole == AgentRole.Developer;
        var now = DateTimeOffset.UtcNow;
        var retryDeferred = DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter);
        var recoverableLimitFailures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        var retryDelaySeconds = retryDeferred
            ? Math.Max(0, (int)Math.Ceiling((retryAfter - now).TotalSeconds))
            : (int?)null;
        var canPrepare = task.Status == WorkTaskStatus.Assigned &&
            hasProfile &&
            !isEchoOnly &&
            pinsSelectedModel &&
            pinsSelectedReasoning &&
            (!requiresPatchCapability || patchCapability.IsPatchCapable) &&
            AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy) &&
            !retryDeferred;
        var estimatedPromptCharacterCount = canPrepare
            ? estimatePromptCharacterCount?.Invoke(task)
            : null;
        var previousLimitFailures = recoverableLimitFailures == 1
            ? "1 previous recoverable subscription usage limit failure"
            : $"{recoverableLimitFailures} previous recoverable subscription usage limit failures";
        var detail = canPrepare
            ? recoverableLimitFailures > 0
                ? $"Ready to prepare subscription dispatch after {previousLimitFailures}; inspect model, profile, or timing before redispatch."
                : "Ready to prepare subscription dispatch."
            : !AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy)
                ? $"Agent execution policy is {agent.ExecutionPolicy}; subscription dispatch is disabled."
            : retryDeferred
                ? $"Recoverable subscription usage limit ({previousLimitFailures}); retry after {retryAfter:u}."
            : !hasProfile
                ? $"Worker profile '{profileName}' was not found."
            : isEchoOnly
                ? $"Worker profile '{profileName}' only echoes the prompt path; configure a real launcher before subscription dispatch."
            : !pinsSelectedModel
                ? $"Worker profile '{profileName}' does not include {{subscriptionModelName}}; pin the selected model before subscription dispatch."
            : !pinsSelectedReasoning
                ? $"Worker profile '{profileName}' does not include {{subscriptionReasoningEffort}}; pin the selected reasoning effort before subscription dispatch."
            : requiresPatchCapability && !patchCapability.IsPatchCapable
                ? $"Worker profile '{profileName}' is not patch-capable for Developer tasks: {patchCapability.Detail}"
                : $"Task status is {task.Status}; only assigned tasks are ready for subscription dispatch.";

        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            OutputTextPreview.CreateSummary(task.Description).Text,
            agent.Id.Value,
            agent.Name,
            effectiveProviderName,
            effectiveModelName,
            agent.ExecutionPolicy,
            profileName,
            agent.Subscription?.ModelAlias,
            hasProfile,
            validation?.IsResolvable ?? false,
            isEchoOnly,
            patchCapability.IsPatchCapable,
            canPrepare,
            OutputTextPreview.CreateTimeline(detail).Text,
            retryDeferred ? retryAfter : null,
            retryDelaySeconds,
            taskComplexity,
            subscriptionModelName,
            subscriptionReasoningEffort,
            estimatedPromptCharacterCount,
            recoverableLimitFailures);
    }
    catch (InvalidOperationException ex)
    {
        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            OutputTextPreview.CreateSummary(task.Description).Text,
            agent.Id.Value,
            agent.Name,
            effectiveProviderName,
            effectiveModelName,
            agent.ExecutionPolicy,
            null,
            agent.Subscription?.ModelAlias,
            false,
            false,
            false,
            false,
            false,
            OutputTextPreview.CreateTimeline(ex.Message).Text,
            TaskComplexity: taskComplexity,
            SubscriptionModelName: subscriptionModelName,
            SubscriptionReasoningEffort: subscriptionReasoningEffort);
    }
}

private static string? GetTemplateValue(IReadOnlyDictionary<string, string?> variables, string name)
{
    return variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : null;
}

private static TaskComplexity? TryParseTaskComplexity(string? value)
{
    return Enum.TryParse<TaskComplexity>(value, ignoreCase: true, out var parsed)
        ? parsed
        : null;
}

private static List<SubscriptionPlanModelSummaryDto> BuildSubscriptionPlanModelSummary(Goal goal, IReadOnlyList<SubscriptionPlanItemDto> items)
{
    var fitByModel = ModelFitEvidence
        .BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes))
        .ToDictionary(fit => BuildModelFitKey(fit.ProviderName, fit.ModelName), StringComparer.OrdinalIgnoreCase);

    return items
        .Where(item => item.CanPrepare && !string.IsNullOrWhiteSpace(item.ProviderName))
        .Select(item => new
        {
            ProviderName = item.ProviderName!,
            ModelName = item.SubscriptionModelName ?? item.SubscriptionModelAlias ?? item.ModelName,
            item.TaskComplexity,
            item.SubscriptionReasoningEffort,
            item.EstimatedPromptCharacterCount
        })
        .Where(item => !string.IsNullOrWhiteSpace(item.ModelName))
        .GroupBy(item => new
        {
            item.ProviderName,
            item.ModelName,
            item.TaskComplexity,
            item.SubscriptionReasoningEffort
        })
        .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.Key.TaskComplexity?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        .ThenBy(group => group.Key.SubscriptionReasoningEffort ?? string.Empty, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            fitByModel.TryGetValue(BuildModelFitKey(group.Key.ProviderName, group.Key.ModelName!), out var fit);
            return new SubscriptionPlanModelSummaryDto(
                group.Key.ProviderName,
                group.Key.ModelName!,
                group.Count(),
                group.Key.TaskComplexity,
                group.Key.SubscriptionReasoningEffort,
                IsPotentiallyPaidProvider(group.Key.ProviderName),
                SumKnownUsage(group.Select(item => item.EstimatedPromptCharacterCount)),
                fit?.NoteCount ?? 0,
                fit?.AdequateCount ?? 0,
                fit?.OverkillCount ?? 0,
                fit?.UnderpoweredCount ?? 0,
                fit?.UnknownCount ?? 0,
                fit?.TaskShapes ?? [],
                BuildModelFitRecommendation(fit, group.Key.ProviderName, group.Key.ModelName!));
        })
        .ToList();
}

private static string BuildModelFitKey(string providerName, string modelName)
{
    return $"{providerName}/{modelName}";
}

private static string? BuildModelFitRecommendation(ModelFitSummary? fit, string providerName, string modelName)
{
    if (fit is null)
    {
        return null;
    }

    if (fit.UnderpoweredCount > 0)
    {
        return $"Prior evidence says {providerName}/{modelName} was underpowered; choose a stronger model before repeating it.";
    }

    if (fit.OverkillCount > 0 && IsPotentiallyPaidProvider(providerName))
    {
        return CostRecommendationText.PaidStartOverkill(providerName, modelName);
    }

    return null;
}

private static int? SumKnownUsage(IEnumerable<int?> values)
{
    var known = values.Where(value => value is not null).Select(value => value!.Value).ToList();
    return known.Count == 0 ? null : known.Sum();
}

private static bool IsPotentiallyPaidProvider(string providerName)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
}

private static bool RequiresSubscriptionReasoningPlaceholder(string providerName, string? reasoningEffort)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(reasoningEffort);
}
}
