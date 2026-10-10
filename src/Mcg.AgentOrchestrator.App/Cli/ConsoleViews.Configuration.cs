using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoals(AgentOrchestratorKernel kernel)
    {
        foreach (var goal in kernel.Goals.OrderByDescending(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue))
        {
            var condition = GoalLifecycle.HasActiveFailedTask(goal)
                ? $" [{GoalLifecycle.ActiveWithFailedTaskCondition}]"
                : string.Empty;
            Console.WriteLine($"{goal.Id.Value[..8]} {goal.Status}{condition}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
        }
    }

    // Metadata-only listing: renders the same line as the kernel overload from GoalSummary rows
    // (already ordered by the repository) without hydrating any goal snapshot.
    public static void PrintGoals(IReadOnlyList<GoalSummary> goals)
    {
        foreach (var goal in goals)
        {
            var prefix = goal.Id.Length >= 8 ? goal.Id[..8] : goal.Id;
            var condition = string.IsNullOrWhiteSpace(goal.Condition) ? string.Empty : $" [{goal.Condition}]";
            Console.WriteLine($"{prefix} {goal.Status}{condition}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
        }
    }

    public static void PrintCleanupDebtWarning(IReadOnlyList<GoalWorktreeCleanupDebt> debts)
    {
        var escalatedCount = debts.Count(debt => debt.EscalatedAtUtc is not null);
        if (escalatedCount > 0)
        {
            Console.WriteLine(
                $"WARNING: stale worktree cleanup escalated={escalatedCount} pending={debts.Count}; run cleanup-status.");
        }
    }

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

    public static void PrintTenant(OrchestratorWorkspace workspace)
    {
        Console.WriteLine($"Tenant: {workspace.TenantName}");
        Console.WriteLine($"Tenant scoped: {workspace.IsTenantScoped}");
        Console.WriteLine($"State: {workspace.SqliteStatePath}");
        Console.WriteLine($"Agents: {workspace.AgentCatalogPath}");
        Console.WriteLine($"Workers: {workspace.WorkerProfilePath}");
        Console.WriteLine($"Continuations: {workspace.ContinuationStorePath}");
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

    public static void PrintArchitecture(DistributedArchitectureReport report)
    {
        Console.WriteLine("Architecture:");
        Console.WriteLine($"  tenant: {report.TenantName} scoped={report.TenantScoped}");
        Console.WriteLine($"  state: {report.StatePath}");
        Console.WriteLine($"  agents: {report.AgentCatalogPath}");
        Console.WriteLine($"  workers: {report.WorkerProfilePath} ({report.UsableWorkerProfileCount}/{report.WorkerProfileCount} usable)");
        Console.WriteLine($"  execution: {report.ExecutionDirectory}");
        Console.WriteLine($"  continuations: {report.ContinuationStorePath}");
        Console.WriteLine($"  providers: {report.ProviderCount}; agents: {report.AgentCount}");
        Console.WriteLine($"  persistence: {report.Persistence}");
        Console.WriteLine($"  subscriptions: {report.SubscriptionWorkers}");
        Console.WriteLine("  state stores:");
        foreach (var store in report.StateStores)
        {
            Console.WriteLine($"    - {store}");
        }

        Console.WriteLine("  safety gates:");
        foreach (var gate in report.SafetyGates)
        {
            Console.WriteLine($"    - {gate}");
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


