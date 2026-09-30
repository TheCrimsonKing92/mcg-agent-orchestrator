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
    return AgentDefinitionFactory.Create(new AgentDefinitionInput(
        submission.Role,
        submission.ProviderName,
        submission.ModelName,
        submission.Name,
        submission.ReasoningEffort,
        submission.MaxOutputTokens,
        submission.ExecutionPolicy,
        submission.SubscriptionProfileName,
        submission.SubscriptionModelAlias,
        submission.SubscriptionReasoningEffort,
        submission.ComplexProviderName,
        submission.ComplexModelName,
        submission.ComplexMaxOutputTokens,
        submission.ComplexReasoningEffort));
}

private static SubscriptionMode DefaultSubscriptionMode(string providerName)
{
    return providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase)
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

    if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
    {
        return WorkerProfileDispatcher.OllamaSubscriptionProfileName;
    }

    if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase))
    {
        return WorkerProfileDispatcher.XaiSubscriptionProfileName;
    }

    if (providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase))
    {
        return WorkerProfileDispatcher.LlamaCppSubscriptionProfileName;
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
        return AgentCatalog.OpenAiSubscriptionModelAlias;
    }

    if (providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase))
    {
        return AgentCatalog.XaiSubscriptionModelAlias;
    }

    if (providerName.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase))
    {
        return LlamaCppDefaults.DefaultModelAlias;
    }

    // Anthropic API model ids are valid claude CLI model names, so the CLI uses the
    // agent's model unless the operator pins an explicit alias such as "sonnet".
    return null;
}

private static string? DefaultSubscriptionReasoningEffort(string providerName, AgentExecutionPolicy executionPolicy, string? subscriptionModelAlias)
{
    return AgentExecutionPolicies.AllowsSubscription(executionPolicy)
        ? AgentCatalog.DefaultSubscriptionReasoningEffort(providerName, subscriptionModelAlias)
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

private static ModelProfile? DefaultComplexModel(string providerName)
{
    if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    {
        return new ModelProfile(
            "OpenAI",
            "gpt-5.5",
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey,
            AgentCatalog.ComplexReasoningEffort,
            AgentCatalog.ComplexApiMaxOutputTokens);
    }

    if (providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
    {
        return new ModelProfile(
            "Anthropic",
            AgentCatalog.AnthropicComplexModelName,
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey,
            null,
            AgentCatalog.ComplexApiMaxOutputTokens);
    }

    return null;
}

private static bool IsPaidProvider(string providerName)
{
    return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) ||
        providerName.Equals("xAI", StringComparison.OrdinalIgnoreCase);
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
        throw new ArgumentException(ProviderSmokeRunner.BuildBroadSmokeConfirmationMessage("confirmAll=true"));
    }

    if (ProviderSmokeRunner.RequiresPaidConfirmation(target) && !confirmAll && !confirmPaidSmoke)
    {
        throw new ArgumentException(ProviderSmokeRunner.BuildPaidSmokeConfirmationMessage("confirmPaidSmoke=true"));
    }
}
}
