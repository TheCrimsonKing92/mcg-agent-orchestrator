namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelProfile(
    string ProviderName,
    string ModelName,
    ModelCapability Capabilities,
    SubscriptionMode SubscriptionMode,
    string? ReasoningEffort = null,
    int? MaxOutputTokens = null);

public sealed record SubscriptionLaunchProfile(
    string WorkerProfileName,
    string? ModelAlias = null,
    string? ReasoningEffort = null);

public sealed record AgentDefinition(
    AgentId Id,
    string Name,
    AgentRole Role,
    ModelProfile Model,
    AgentStatus Status = AgentStatus.Available,
    AgentExecutionPolicy ExecutionPolicy = AgentExecutionPolicy.ApiOnly,
    SubscriptionLaunchProfile? Subscription = null);

public static class AgentExecutionPolicies
{
    public static bool AllowsApi(AgentExecutionPolicy policy)
    {
        return policy is AgentExecutionPolicy.ApiOnly or AgentExecutionPolicy.PreferSubscription or AgentExecutionPolicy.AnyAvailable;
    }

    public static bool AllowsSubscription(AgentExecutionPolicy policy)
    {
        return policy is AgentExecutionPolicy.SubscriptionOnly or AgentExecutionPolicy.PreferSubscription or AgentExecutionPolicy.AnyAvailable;
    }
}
