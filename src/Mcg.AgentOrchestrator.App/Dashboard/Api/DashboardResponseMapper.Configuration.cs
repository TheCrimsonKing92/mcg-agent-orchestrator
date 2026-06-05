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
        agent.Subscription?.ReasoningEffort);
}

public static IReadOnlyList<WorkerProfileDto> ToWorkerProfileDtos(string agentCatalogPath, WorkerProfileCatalog catalog)
{
    var validations = OrchestratorHealthInspector
        .InspectCurrentEnvironment(AgentCatalogStore.Load(agentCatalogPath), catalog)
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

public static SubscriptionPlanDto BuildSubscriptionPlan(Goal goal, IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
{
    var validations = OrchestratorHealthInspector
        .InspectCurrentEnvironment(new AgentCatalog(agents), profiles)
        .WorkerProfiles
        .ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);

    var items = goal.Tasks
        .Select(task => BuildSubscriptionPlanItem(goal, task, agents, profiles, validations))
        .ToList();

    return new SubscriptionPlanDto(
        goal.Id.Value,
        goal.Objective,
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
        items);
}

public static SubscriptionPlanItemDto BuildSubscriptionPlanItem(
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IReadOnlyDictionary<string, WorkerProfileValidation> validations)
{
    var taskNumber = ConsoleViews.GetTaskDisplayNumber(goal, task.Id);
    if (task.AssignedAgentId is null)
    {
        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            task.Description,
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
            task.Description,
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

    try
    {
        var profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent);
        var profile = profiles.Profiles.FirstOrDefault(candidate => candidate.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        validations.TryGetValue(profileName, out var validation);
        var hasProfile = profile is not null;
        var isEchoOnly = profile is not null && WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate);
        var patchCapability = profile is null
            ? new WorkerProfilePatchCapability(false, "Worker profile was not found.")
            : WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
        var requiresPatchCapability = task.RequiredRole == AgentRole.Developer;
        var now = DateTimeOffset.UtcNow;
        var retryDeferred = DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter);
        var retryDelaySeconds = retryDeferred
            ? Math.Max(0, (int)Math.Ceiling((retryAfter - now).TotalSeconds))
            : (int?)null;
        var canPrepare = task.Status == WorkTaskStatus.Assigned &&
            hasProfile &&
            !isEchoOnly &&
            (!requiresPatchCapability || patchCapability.IsPatchCapable) &&
            AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy) &&
            !retryDeferred;
        var detail = canPrepare
            ? "Ready to prepare subscription dispatch."
            : !AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy)
                ? $"Agent execution policy is {agent.ExecutionPolicy}; subscription dispatch is disabled."
            : retryDeferred
                ? $"Recoverable subscription usage limit; retry after {retryAfter:u}."
            : !hasProfile
                ? $"Worker profile '{profileName}' was not found."
            : isEchoOnly
                ? $"Worker profile '{profileName}' only echoes the prompt path; configure a real launcher before subscription dispatch."
            : requiresPatchCapability && !patchCapability.IsPatchCapable
                ? $"Worker profile '{profileName}' is not patch-capable for Developer tasks: {patchCapability.Detail}"
                : $"Task status is {task.Status}; only assigned tasks are ready for subscription dispatch.";

        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            task.Description,
            agent.Id.Value,
            agent.Name,
            agent.Model.ProviderName,
            agent.Model.ModelName,
            agent.ExecutionPolicy,
            profileName,
            agent.Subscription?.ModelAlias,
            hasProfile,
            validation?.IsResolvable ?? false,
            isEchoOnly,
            patchCapability.IsPatchCapable,
            canPrepare,
            detail,
            retryDeferred ? retryAfter : null,
            retryDelaySeconds);
    }
    catch (InvalidOperationException ex)
    {
        return new SubscriptionPlanItemDto(
            taskNumber,
            task.Id.Value,
            task.RequiredRole,
            task.Status,
            task.Description,
            agent.Id.Value,
            agent.Name,
            agent.Model.ProviderName,
            agent.Model.ModelName,
            agent.ExecutionPolicy,
            null,
            agent.Subscription?.ModelAlias,
            false,
            false,
            false,
            false,
            false,
            ex.Message);
    }
}
}
