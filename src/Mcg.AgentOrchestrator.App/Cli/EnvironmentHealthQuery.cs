using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EnvironmentHealthQuery
{
    internal static OrchestratorHealthReport InspectCurrentEnvironment(
        AgentCatalog agents, WorkerProfileCatalog catalog, string? name, Func<string, bool>? commandExists = null)
    {
        var selected = string.IsNullOrWhiteSpace(name)
            ? catalog
            : new WorkerProfileCatalog([catalog.GetRequired(name)]);
        return OrchestratorHealthInspector.InspectCurrentEnvironment(agents, selected, commandExists);
    }

    internal static IEnumerable<(AgentRole Role, string ProfileName, string Detail)> BuildSubscriptionRouteIssues(
        OrchestratorHealthReport report,
        string? name)
    {
        var profiles = report.WorkerProfiles.ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var agent in report.Agents.Where(agent => AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy)))
        {
            if (string.IsNullOrWhiteSpace(agent.SubscriptionProfileName))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name) &&
                !agent.SubscriptionProfileName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!profiles.TryGetValue(agent.SubscriptionProfileName, out var profile))
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile is not configured");
                continue;
            }

            if (!profile.IsResolvable)
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile is not executable");
                continue;
            }

            if (profile.IsEchoOnly)
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile only echoes the prompt path");
                continue;
            }

            if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile does not pin the selected model");
                continue;
            }

            if (RequiresSubscriptionReasoningPlaceholder(agent.ProviderName, agent.SubscriptionReasoningEffort ?? agent.ReasoningEffort) &&
                !WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile does not pin the selected reasoning effort");
                continue;
            }

            if (agent.Role == AgentRole.Developer && !profile.IsPatchCapable)
            {
                yield return (agent.Role, agent.SubscriptionProfileName, "subscription profile cannot patch Developer tasks");
            }
        }
    }

    private static bool RequiresSubscriptionReasoningPlaceholder(string providerName, string? reasoningEffort)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(reasoningEffort);
    }

}
