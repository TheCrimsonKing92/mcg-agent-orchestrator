using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class WorkerConfigurationTextView
{
    public static void PrintModelFunctions(ModelFunctionCatalog catalog)
    {
        if (catalog.Bindings.Count == 0)
        {
            Console.WriteLine("Model functions: (none configured)");
            return;
        }

        Console.WriteLine("Model functions (orchestrator-internal model uses, not workers):");
        foreach (var binding in catalog.Bindings)
        {
            var name = string.IsNullOrWhiteSpace(binding.Name) ? string.Empty : $" ({binding.Name})";
            Console.WriteLine($"  {binding.Purpose} [{binding.Lane}]: {binding.Model.ProviderName}/{binding.Model.ModelName}{name}");
        }
    }

    public static void PrintWorkerProfiles(WorkerProfileCatalog catalog)
    {
        Console.WriteLine("Worker profiles:");
        foreach (var profile in catalog.Profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {profile.Name}: {profile.CommandTemplate}");
        }
    }

    public static void PrintWorkerProfileChecks(AgentCatalog agents, WorkerProfileCatalog catalog, string? name, Func<string, bool>? commandExists = null)
    {
        var report = EnvironmentHealthQuery.InspectCurrentEnvironment(agents, catalog, name, commandExists);

        Console.WriteLine("Worker profile checks:");
        foreach (var profile in report.WorkerProfiles)
        {
            Console.WriteLine($"  {profile.Name}: executable={profile.Executable} resolvable={profile.IsResolvable} patchCapable={profile.IsPatchCapable} ({profile.Detail})");
        }

        var routeIssues = EnvironmentHealthQuery.BuildSubscriptionRouteIssues(report, name).ToList();
        foreach (var issue in routeIssues)
        {
            Console.WriteLine($"  route {issue.Role}: profile={issue.ProfileName} ({issue.Detail})");
        }

        if (report.WorkerProfiles.Any(profile => !profile.IsResolvable))
        {
            throw new InvalidOperationException("One or more worker profiles are not resolvable.");
        }

        if (routeIssues.Count > 0)
        {
            throw new InvalidOperationException("One or more active subscription routes are not usable.");
        }
    }

    public static void PrintAgents(IReadOnlyList<AgentDefinition> agents)
    {
        Console.WriteLine("Agents:");
        var primaryAgentIds = agents
            .Where(agent => agent.Status == AgentStatus.Available)
            .GroupBy(agent => agent.Role)
            .ToDictionary(group => group.Key, group => group.First().Id);
        foreach (var agent in agents.OrderBy(agent => agent.Role))
        {
            var subscription = agent.Subscription is null
                ? "none"
                : $"{agent.Subscription.WorkerProfileName} model={agent.Subscription.ModelAlias ?? "default"} reasoning={agent.Subscription.ReasoningEffort ?? "default"}";
            var route = primaryAgentIds.TryGetValue(agent.Role, out var primaryId) && primaryId == agent.Id
                ? "primary"
                : "alternate";
            var providerRouting = agent.IsProviderRoutingConstrained switch
            {
                true => "constrained",
                false => "automatic",
                null => "unspecified"
            };
            Console.WriteLine($"  {agent.Role}: {agent.Name} id={agent.Id.Value} route={route} provider-routing={providerRouting} execution={agent.ExecutionPolicy} api={agent.Model.ProviderName}/{agent.Model.ModelName} reasoning={agent.Model.ReasoningEffort ?? "default"} subscription={subscription} status={agent.Status}");
        }
    }
}
