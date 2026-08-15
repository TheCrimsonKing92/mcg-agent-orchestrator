using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
// Adds/replaces an orchestrator-internal model-function binding (e.g. an acceptance-judge lane).
// Distinct from agents: these are models the orchestrator invokes for its own functions, not workers.
private static void AddModelFunctionBinding(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var purpose = parts[1];
    var lane = CliArgumentParser.ParseModelLane(parts[2]);
    var provider = parts[3];
    var model = parts[4];
    var name = parts.Count > 5 && !parts[5].StartsWith("--", StringComparison.Ordinal) ? parts[5] : null;
    var subscriptionMode = lane == ModelLane.Local ? SubscriptionMode.LocalBridge : SubscriptionMode.ApiKey;
    var subscriptionProfileName = GetFlagValue(parts, "--subscription");
    var subscriptionModelAlias = GetFlagValue(parts, "--subscription-model");
    var subscriptionReasoning = GetFlagValue(parts, "--subscription-reasoning");
    SubscriptionLaunchProfile? subscription = subscriptionProfileName is not null
        ? new SubscriptionLaunchProfile(subscriptionProfileName, subscriptionModelAlias, subscriptionReasoning)
        : null;
    var binding = new ModelFunctionBinding(
        purpose,
        lane,
        new ModelProfile(provider, model, ModelCapability.Text, subscriptionMode),
        name,
        subscription);

    var path = context.Workspace.ModelFunctionCatalogPath;
    var bindings = ModelFunctionCatalogStore.Load(path).Bindings
        .Where(existing => !(string.Equals(existing.Purpose, purpose, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Model.ProviderName, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Model.ModelName, model, StringComparison.OrdinalIgnoreCase)))
        .Append(binding)
        .ToList();

    var catalog = new ModelFunctionCatalog(bindings);
    ModelFunctionCatalogStore.Save(path, catalog);
    ConsoleViews.PrintModelFunctions(catalog);
}

private static AgentDefinition CreateCliAgentDefinition(IReadOnlyList<string> parts)
{
    var agentName = parts.Count > 4 && !parts[4].StartsWith("--", StringComparison.Ordinal) ? parts[4] : null;
    var complexModelName = GetFlagValue(parts, "--complex-model");
    var subscriptionModel = GetFlagValue(parts, "--subscription-model");
    var subscriptionReasoning = GetFlagValue(parts, "--subscription-reasoning");
    return DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        parts[1], parts[2], parts[3], agentName,
        SubscriptionModelAlias: subscriptionModel,
        SubscriptionReasoningEffort: subscriptionReasoning,
        ComplexProviderName: complexModelName is null ? null : parts[2],
        ComplexModelName: complexModelName));
}

private static string GetCliAgentIdSuffix(IReadOnlyList<string> parts, AgentDefinition agent)
{
    return parts.Count > 4 && !parts[4].StartsWith("--", StringComparison.Ordinal)
        ? parts[4]
        : agent.Model.ModelName;
}

private static AgentId BuildAlternateAgentId(AgentDefinition agent, string suffix)
{
    var baseId = $"{Slug(agent.Model.ProviderName)}-{Slug(agent.Role.ToString())}";
    var suffixSlug = Slug(suffix);
    return new AgentId(string.IsNullOrWhiteSpace(suffixSlug) ? baseId : $"{baseId}-{suffixSlug}");
}

private static string Slug(string value)
{
    var chars = value
        .Trim()
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
        .ToArray();
    var slug = new string(chars);
    while (slug.Contains("--", StringComparison.Ordinal))
    {
        slug = slug.Replace("--", "-", StringComparison.Ordinal);
    }

    return slug.Trim('-');
}

internal static IReadOnlyList<AgentDefinition> ApplyRoleAgentOverrides(
    IReadOnlyList<string> parts,
    IReadOnlyList<AgentDefinition> agents)
{
    var catalog = new AgentCatalog(agents);
    foreach (var (flag, role) in GoalRoleAgentFlags)
    {
        var agentId = GetFlagValue(parts, flag);
        if (agentId is null && !HasCliConfirmation(parts, flag))
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(agentId) || agentId.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires <agentId>.");
        }

        var agent = catalog.FindById(agentId)
            ?? throw new ArgumentException($"Unknown agent id '{agentId}' for {flag}.");
        if (agent.Role != role)
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' has role {agent.Role}; {flag} requires an agent with role {role}.");
        }

        catalog = catalog.UpsertRole(agent);
    }

    return catalog.Agents;
}

private static void WarnAboutTasksPinnedToRemovedAgent(AgentOrchestratorKernel kernel, AgentDefinition? previousAgent, AgentDefinition replacementAgent)
{
    if (previousAgent is null || previousAgent.Id == replacementAgent.Id)
    {
        return;
    }

    var affected = kernel.Goals
        .Where(goal => goal.Status is GoalStatus.Active or GoalStatus.WaitingForHuman)
        .SelectMany(goal => goal.Tasks
            .Where(task => task.AssignedAgentId == previousAgent.Id &&
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
            .Select(task => $"{goal.Id.Value[..8]}:{task.Id.Value}"))
        .ToList();
    if (affected.Count == 0)
    {
        return;
    }

    Console.Error.WriteLine(
        $"Warning: replaced {previousAgent.Role} agent '{previousAgent.Id.Value}' with '{replacementAgent.Id.Value}', but in-flight task(s) remain pinned to the removed agent: {string.Join(", ", affected)}. Use reassign-agent to update them deliberately.");
}
}
