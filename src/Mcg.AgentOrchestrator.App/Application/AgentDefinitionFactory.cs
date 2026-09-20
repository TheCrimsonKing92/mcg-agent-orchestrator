using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

public sealed record AgentDefinitionInput(
    string Role,
    string ProviderName,
    string ModelName,
    string? Name = null,
    string? ReasoningEffort = null,
    int? MaxOutputTokens = null,
    string? ExecutionPolicy = null,
    string? SubscriptionProfileName = null,
    string? SubscriptionModelAlias = null,
    string? SubscriptionReasoningEffort = null,
    string? ComplexProviderName = null,
    string? ComplexModelName = null,
    int? ComplexMaxOutputTokens = null,
    string? ComplexReasoningEffort = null);

public static class AgentDefinitionFactory
{
    public static AgentDefinition Create(AgentDefinitionInput input)
    {
        var role = CliArgumentParser.ParseAgentRole(input.Role);
        var providerName = input.ProviderName.Trim();
        var modelName = input.ModelName.Trim();
        var executionPolicy = ParseExecutionPolicy(input.ExecutionPolicy, providerName);
        var name = string.IsNullOrWhiteSpace(input.Name)
            ? $"{providerName} {role.ToString().ToLowerInvariant()}"
            : input.Name;
        var allowsSubscription = AgentExecutionPolicies.AllowsSubscription(executionPolicy);
        var subscriptionProfileName = allowsSubscription
            ? string.IsNullOrWhiteSpace(input.SubscriptionProfileName)
                ? DefaultSubscriptionProfileName(providerName, executionPolicy)
                : input.SubscriptionProfileName
            : null;
        var subscriptionModelAlias = allowsSubscription
            ? string.IsNullOrWhiteSpace(input.SubscriptionModelAlias)
                ? DefaultSubscriptionModelAlias(providerName, executionPolicy)
                : input.SubscriptionModelAlias
            : null;
        var subscriptionReasoningEffort = allowsSubscription
            ? string.IsNullOrWhiteSpace(input.SubscriptionReasoningEffort)
                ? DefaultSubscriptionReasoningEffort(providerName, executionPolicy, subscriptionModelAlias)
                : input.SubscriptionReasoningEffort
            : null;
        var subscription = string.IsNullOrWhiteSpace(subscriptionProfileName)
            ? null
            : new SubscriptionLaunchProfile(
                subscriptionProfileName,
                string.IsNullOrWhiteSpace(subscriptionModelAlias) ? null : subscriptionModelAlias,
                string.IsNullOrWhiteSpace(subscriptionReasoningEffort) ? null : subscriptionReasoningEffort);
        var complexModel = !string.IsNullOrWhiteSpace(input.ComplexProviderName) && !string.IsNullOrWhiteSpace(input.ComplexModelName)
            ? new ModelProfile(
                input.ComplexProviderName,
                input.ComplexModelName,
                ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                DefaultSubscriptionMode(input.ComplexProviderName),
                string.IsNullOrWhiteSpace(input.ComplexReasoningEffort) ? null : input.ComplexReasoningEffort,
                MaxOutputTokens: input.ComplexMaxOutputTokens ?? DefaultComplexMaxOutputTokens(input.ComplexProviderName))
            : DefaultComplexModel(providerName);

        return new AgentDefinition(
            new AgentId($"{providerName.ToLowerInvariant()}-{role.ToString().ToLowerInvariant()}"),
            name,
            role,
            new ModelProfile(
                providerName,
                modelName,
                ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                DefaultSubscriptionMode(providerName),
                string.IsNullOrWhiteSpace(input.ReasoningEffort) ? DefaultReasoningEffort(providerName) : input.ReasoningEffort,
                input.MaxOutputTokens ?? DefaultMaxOutputTokens(providerName)),
            ExecutionPolicy: executionPolicy,
            Subscription: subscription,
            ComplexModel: complexModel,
            IsProviderRoutingConstrained: true);
    }

    private static SubscriptionMode DefaultSubscriptionMode(string providerName) =>
        providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase)
            ? SubscriptionMode.LocalBridge
            : SubscriptionMode.ApiKey;

    private static AgentExecutionPolicy ParseExecutionPolicy(string? value, string providerName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return IsPaidProvider(providerName) ? AgentExecutionPolicy.PreferSubscription : AgentExecutionPolicy.ApiOnly;

        return Enum.TryParse<AgentExecutionPolicy>(value, ignoreCase: true, out var policy)
            ? policy
            : throw new ArgumentException("Execution policy must be ApiOnly, SubscriptionOnly, PreferSubscription, or AnyAvailable.");
    }

    private static string? DefaultSubscriptionProfileName(string providerName, AgentExecutionPolicy executionPolicy)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(executionPolicy)) return null;
        if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return "codex-cli";
        if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)) return "claude-cli";
        if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) return WorkerProfileDispatcher.OllamaSubscriptionProfileName;
        if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)) return WorkerProfileDispatcher.XaiSubscriptionProfileName;
        return providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase)
            ? WorkerProfileDispatcher.LlamaCppSubscriptionProfileName
            : null;
    }

    private static string? DefaultSubscriptionModelAlias(string providerName, AgentExecutionPolicy executionPolicy)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(executionPolicy)) return null;
        if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return AgentCatalog.OpenAiSubscriptionModelAlias;
        if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase)) return "grok-4.6";
        return providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase)
            ? LlamaCppDefaults.DefaultModelAlias
            : null;
    }

    private static string? DefaultSubscriptionReasoningEffort(
        string providerName,
        AgentExecutionPolicy executionPolicy,
        string? subscriptionModelAlias) =>
        AgentExecutionPolicies.AllowsSubscription(executionPolicy)
            ? AgentCatalog.DefaultSubscriptionReasoningEffort(providerName, subscriptionModelAlias)
            : null;

    private static string? DefaultReasoningEffort(string providerName) =>
        providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ? AgentCatalog.RoutineReasoningEffort : null;

    private static int? DefaultMaxOutputTokens(string providerName) =>
        IsPaidProvider(providerName) ? AgentCatalog.RoutineApiMaxOutputTokens : null;

    private static int? DefaultComplexMaxOutputTokens(string providerName) =>
        IsPaidProvider(providerName) ? AgentCatalog.ComplexApiMaxOutputTokens : null;

    private static ModelProfile? DefaultComplexModel(string providerName)
    {
        if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, AgentCatalog.ComplexReasoningEffort, AgentCatalog.ComplexApiMaxOutputTokens);
        if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, null, AgentCatalog.ComplexApiMaxOutputTokens);
        return null;
    }

    private static bool IsPaidProvider(string providerName) =>
        providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase);
}
