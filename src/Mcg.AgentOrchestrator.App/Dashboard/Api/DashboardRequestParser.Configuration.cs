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
        string.IsNullOrWhiteSpace(submission.ExecutionPolicy) ? null : submission.ExecutionPolicy.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionProfileName) ? null : submission.SubscriptionProfileName.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionModelAlias) ? null : submission.SubscriptionModelAlias.Trim(),
        string.IsNullOrWhiteSpace(submission.SubscriptionReasoningEffort) ? null : submission.SubscriptionReasoningEffort.Trim());
}

public static AgentDefinition CreateAgentDefinition(AgentSubmissionDto submission)
{
    var role = CliArgumentParser.ParseAgentRole(submission.Role);
    var executionPolicy = ParseExecutionPolicy(submission.ExecutionPolicy);
    var name = string.IsNullOrWhiteSpace(submission.Name)
        ? $"{submission.ProviderName} {role.ToString().ToLowerInvariant()}"
        : submission.Name;
    var subscription = string.IsNullOrWhiteSpace(submission.SubscriptionProfileName)
        ? null
        : new SubscriptionLaunchProfile(
            submission.SubscriptionProfileName,
            string.IsNullOrWhiteSpace(submission.SubscriptionModelAlias) ? null : submission.SubscriptionModelAlias,
            string.IsNullOrWhiteSpace(submission.SubscriptionReasoningEffort) ? null : submission.SubscriptionReasoningEffort);
    return new AgentDefinition(
        new AgentId($"{submission.ProviderName.ToLowerInvariant()}-{role.ToString().ToLowerInvariant()}"),
        name,
        role,
        new ModelProfile(
            submission.ProviderName,
            submission.ModelName,
            ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
            SubscriptionMode.ApiKey,
            string.IsNullOrWhiteSpace(submission.ReasoningEffort) ? null : submission.ReasoningEffort),
        ExecutionPolicy: executionPolicy,
        Subscription: subscription);
}

private static AgentExecutionPolicy ParseExecutionPolicy(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return AgentExecutionPolicy.ApiOnly;
    }

    if (Enum.TryParse<AgentExecutionPolicy>(value, ignoreCase: true, out var policy))
    {
        return policy;
    }

    throw new ArgumentException("Execution policy must be ApiOnly, SubscriptionOnly, PreferSubscription, or AnyAvailable.");
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
        return "all";
    }

    if (!trimmed.StartsWith("{", StringComparison.Ordinal))
    {
        return trimmed;
    }

    var submission = JsonSerializer.Deserialize<ProviderSmokeSubmissionDto>(trimmed, DashboardJson.Options());
    return string.IsNullOrWhiteSpace(submission?.Target) ? "all" : submission.Target.Trim();
}
}
