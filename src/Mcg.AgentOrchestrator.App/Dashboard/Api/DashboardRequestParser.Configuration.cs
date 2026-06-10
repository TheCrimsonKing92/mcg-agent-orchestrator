using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardRequestParser
{
public static AgentSubmissionDto ParseAgentSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Agent JSON body cannot be empty.");
    }

    var submission = JsonSerializer.Deserialize<AgentSubmissionDto>(body, DashboardJson.Options());
    if (submission is null)
    {
        throw new ArgumentException("Agent JSON body was invalid.");
    }

    if (string.IsNullOrWhiteSpace(submission.Role))
    {
        throw new ArgumentException("Agent JSON must include a non-empty 'role' value.");
    }

    if (string.IsNullOrWhiteSpace(submission.ProviderName))
    {
        throw new ArgumentException("Agent JSON must include a non-empty 'providerName' value.");
    }

    if (string.IsNullOrWhiteSpace(submission.ModelName))
    {
        throw new ArgumentException("Agent JSON must include a non-empty 'modelName' value.");
    }

    return new AgentSubmissionDto(
        submission.Role.Trim(),
        submission.ProviderName.Trim(),
        submission.ModelName.Trim(),
        string.IsNullOrWhiteSpace(submission.Name) ? null : submission.Name.Trim(),
        string.IsNullOrWhiteSpace(submission.ReasoningEffort) ? null : submission.ReasoningEffort.Trim(),
        submission.MaxOutputTokens,
        string.IsNullOrWhiteSpace(submission.ExecutionPolicy) ? null : submission.ExecutionPolicy.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionProfileName) ? null : submission.SubscriptionProfileName.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionModelAlias) ? null : submission.SubscriptionModelAlias.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionReasoningEffort) ? null : submission.SubscriptionReasoningEffort.Trim(),
        string.IsNullOrWhiteSpace(submission.ComplexProviderName) ? null : submission.ComplexProviderName.Trim(),
        string.IsNullOrWhiteSpace(submission.ComplexModelName) ? null : submission.ComplexModelName.Trim(),
        submission.ComplexMaxOutputTokens,
        string.IsNullOrWhiteSpace(submission.ComplexReasoningEffort) ? null : submission.ComplexReasoningEffort.Trim());
}

public static AgentDefinition CreateAgentDefinition(AgentSubmissionDto submission)
{
    var role = CliArgumentParser.ParseAgentRole(submission.Role);
    var providerName = submission.ProviderName.Trim();
    var modelName = submission.ModelName.Trim();
    var executionPolicy = ParseExecutionPolicy(submission.ExecutionPolicy, providerName);
    var name = string.IsNullOrWhiteSpace(submission.Name)
        ? $"{providerName} {role.ToString().ToLowerInvariant()}"
        : submission.Name;
    var allowsSubscription = AgentExecutionPolicies.AllowsSubscription(executionPolicy);
    var subscriptionProfileName = allowsSubscription
        ? string.IsNullOrWhiteSpace(submission.SubscriptionProfileName)
            ? DefaultSubscriptionProfileName(providerName, executionPolicy)
            : submission.SubscriptionProfileName
        : null;
    var subscriptionModelAlias = allowsSubscription
        ? string.IsNullOrWhiteSpace(submission.SubscriptionModelAlias)
            ? DefaultSubscriptionModelAlias(providerName, executionPolicy)
            : submission.SubscriptionModelAlias
        : null;
    var subscriptionReasoningEffort = allowsSubscription
        ? string.IsNullOrWhiteSpace(submission.SubscriptionReasoningEffort)
            ? DefaultSubscriptionReasoningEffort(providerName, executionPolicy)
            : submission.SubscriptionReasoningEffort
        : null;
    var subscription = string.IsNullOrWhiteSpace(subscriptionProfileName)
        ? null
        : new SubscriptionLaunchProfile(
            subscriptionProfileName,
            string.IsNullOrWhiteSpace(subscriptionModelAlias) ? null : subscriptionModelAlias,
            string.IsNullOrWhiteSpace(subscriptionReasoningEffort) ? null : subscriptionReasoningEffort);
    var complexModel = !string.IsNullOrWhiteSpace(submission.ComplexProviderName) && !string.IsNullOrWhiteSpace(submission.ComplexModelName)
        ? new ModelProfile(
            submission.ComplexProviderName,
            submission.ComplexModelName,
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            DefaultSubscriptionMode(submission.ComplexProviderName),
            string.IsNullOrWhiteSpace(submission.ComplexReasoningEffort) ? null : submission.ComplexReasoningEffort,
            MaxOutputTokens: submission.ComplexMaxOutputTokens ?? DefaultComplexMaxOutputTokens(submission.ComplexProviderName))
        : null;

    return new AgentDefinition(
        new AgentId($"{providerName.ToLowerInvariant()}-{role.ToString().ToLowerInvariant()}"),
        name,
        role,
        new ModelProfile(
            providerName,
            modelName,
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            DefaultSubscriptionMode(providerName),
            string.IsNullOrWhiteSpace(submission.ReasoningEffort) ? DefaultReasoningEffort(providerName) : submission.ReasoningEffort,
            submission.MaxOutputTokens ?? DefaultMaxOutputTokens(providerName)),
        ExecutionPolicy: executionPolicy,
        Subscription: subscription,
        ComplexModel: complexModel);
}

private static SubscriptionMode DefaultSubscriptionMode(string providerName)
{
    return providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
        ? SubscriptionMode.LocalBridge
        : SubscriptionMode.ApiKey;
}

private static AgentExecutionPolicy ParseExecutionPolicy(string? value, string providerName)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return IsPaidProvider(providerName) ? AgentExecutionPolicy.PreferSubscription : AgentExecutionPolicy.ApiOnly;
    }

    if (Enum.TryParse<AgentExecutionPolicy>(value, ignoreCase: true, out var policy))
    {
        return policy;
    }

    throw new ArgumentException("Execution policy must be ApiOnly, SubscriptionOnly, PreferSubscription, or AnyAvailable.");
}

private static string? DefaultSubscriptionProfileName(string providerName, AgentExecutionPolicy executionPolicy)
{
    if (!AgentExecutionPolicies.AllowsSubscription(executionPolicy))
    {
        return null;
    }

    if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        return "codex-cli";
    }

    if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
    {
        return "claude-cli";
    }

    return null;
}

private static string? DefaultSubscriptionModelAlias(string providerName, AgentExecutionPolicy executionPolicy)
{
    if (!AgentExecutionPolicies.AllowsSubscription(executionPolicy))
    {
        return null;
    }

    if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        return "gpt-5.3-codex";
    }

    return providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        ? "claude-sonnet"
        : null;
}

private static string? DefaultSubscriptionReasoningEffort(string providerName, AgentExecutionPolicy executionPolicy)
{
    return AgentExecutionPolicies.AllowsSubscription(executionPolicy) &&
        providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            ? AgentCatalog.RoutineSubscriptionReasoningEffort
            : null;
}

private static string? DefaultReasoningEffort(string providerName)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        ? AgentCatalog.RoutineReasoningEffort
        : null;
}

private static int? DefaultMaxOutputTokens(string providerName)
{
    return IsPaidProvider(providerName) ? AgentCatalog.RoutineApiMaxOutputTokens : null;
}

private static int? DefaultComplexMaxOutputTokens(string providerName)
{
    return IsPaidProvider(providerName) ? AgentCatalog.ComplexApiMaxOutputTokens : null;
}

private static bool IsPaidProvider(string providerName)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
}

public static WorkerProfileSubmissionDto ParseWorkerProfileSubmission(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new ArgumentException("Worker profile JSON body cannot be empty.");
    }

    var submission = JsonSerializer.Deserialize<WorkerProfileSubmissionDto>(body, DashboardJson.Options());
    if (submission is null)
    {
        throw new ArgumentException("Worker profile JSON body was invalid.");
    }

    if (string.IsNullOrWhiteSpace(submission.Name))
    {
        throw new ArgumentException("Worker profile JSON must include a non-empty 'name' value.");
    }

    if (string.IsNullOrWhiteSpace(submission.CommandTemplate))
    {
        throw new ArgumentException("Worker profile JSON must include a non-empty 'commandTemplate' value.");
    }

    return new WorkerProfileSubmissionDto(submission.Name.Trim(), submission.CommandTemplate.Trim());
}

public static string ParseProviderSmokeSubmission(string body)
{
    var trimmed = body.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        RequireProviderSmokeConfirmation(ProviderSmokeRunner.DefaultTarget, confirmAll: false, confirmPaidSmoke: false);
        return ProviderSmokeRunner.DefaultTarget;
    }

    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        RequireProviderSmokeConfirmation(trimmed, confirmAll: false, confirmPaidSmoke: false);
        return trimmed;
    }

    var submission = JsonSerializer.Deserialize<ProviderSmokeSubmissionDto>(trimmed, DashboardJson.Options());
    var target = string.IsNullOrWhiteSpace(submission?.Target) ? ProviderSmokeRunner.DefaultTarget : submission.Target.Trim();
    RequireProviderSmokeConfirmation(target, submission?.ConfirmAll is true, submission?.ConfirmPaidSmoke is true);
    return target;
}

internal static void RequireProviderSmokeConfirmation(string target, bool confirmAll, bool confirmPaidSmoke)
{
    if (target.Equals("all", StringComparison.OrdinalIgnoreCase) && !confirmAll)
    {
        throw new ArgumentException("Smoking all providers requires confirmAll=true because broad paid smoke tests are deliberate.");
    }

    if (ProviderSmokeRunner.RequiresPaidConfirmation(target) && !confirmAll && !confirmPaidSmoke)
    {
        throw new ArgumentException("Paid provider smoke requires confirmPaidSmoke=true because it can make a live billable request.");
    }
}
}
