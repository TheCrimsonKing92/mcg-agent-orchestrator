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
        Console.WriteLine($"{goal.Id.Value[..8]} {goal.Status}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
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

public static void PrintWorkerProfileChecks(AgentCatalog agents, WorkerProfileCatalog catalog, string? name)
{
    var selected = string.IsNullOrWhiteSpace(name)
        ? catalog
        : new WorkerProfileCatalog([catalog.GetRequired(name)]);
    var report = OrchestratorHealthInspector.InspectCurrentEnvironment(agents, selected);

    Console.WriteLine("Worker profile checks:");
    foreach (var profile in report.WorkerProfiles)
    {
        Console.WriteLine($"  {profile.Name}: executable={profile.Executable} resolvable={profile.IsResolvable} patchCapable={profile.IsPatchCapable} ({profile.Detail})");
    }

    var routeIssues = BuildSubscriptionRouteIssues(report, name).ToList();
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

private static IEnumerable<(AgentRole Role, string ProfileName, string Detail)> BuildSubscriptionRouteIssues(
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

public static void PrintSubscriptionPlan(SubscriptionPlanDto plan)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {plan.GoalId[..8]} subscription plan: ready={plan.ReadyToPrepareCount} resolvable={plan.ResolvableProfileCount}/{plan.Items.Count}");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(plan.Objective).Text}");
    Console.WriteLine($"Status: {plan.Status}");
    if (plan.ReadyModelUsage.Count > 0)
    {
        Console.WriteLine("Ready model usage: " + string.Join("; ", plan.ReadyModelUsage.Select(FormatSubscriptionPlanModelSummary)));
    }

    if (!string.IsNullOrWhiteSpace(plan.ReadyStartCostRisk))
    {
        var prompt = plan.ReadyStartPromptCharacterCount is null
            ? string.Empty
            : $"; {plan.ReadyStartPromptCharacterCount.Value} prompt chars";
        var details = plan.ReadyStartCostRiskDetails.Count == 0
            ? string.Empty
            : " " + string.Join(" ", plan.ReadyStartCostRiskDetails.Take(2));
        Console.WriteLine($"Ready start risk: {plan.ReadyStartCostRisk}{prompt}. Inspect this plan before using {SubscriptionPromptCostGuard.CliConfirmationFlag}.{details}");
    }

    foreach (var item in plan.Items)
    {
        var complexity = item.TaskComplexity is null ? string.Empty : $", {item.TaskComplexity}";
        var agent = item.AgentName is null
            ? "unassigned"
            : $"{item.AgentName} ({item.ProviderName}/{item.ModelName}{complexity}, {item.ExecutionPolicy})";
        var subscriptionModel = item.SubscriptionModelName ?? item.SubscriptionModelAlias ?? "default";
        var subscriptionReasoning = item.SubscriptionReasoningEffort is null ? string.Empty : $" reasoning={item.SubscriptionReasoningEffort}";
        var estimatedPrompt = item.EstimatedPromptCharacterCount is null ? string.Empty : $" estPrompt={item.EstimatedPromptCharacterCount.Value}chars";
        var profile = item.ProfileName is null
            ? "none"
            : $"{item.ProfileName} model={subscriptionModel}{subscriptionReasoning}{estimatedPrompt} profile={item.ProfileExists} executable={item.ProfileIsResolvable} patchCapable={item.ProfileIsPatchCapable}";
        Console.WriteLine($"  {item.TaskNumber}. [{item.TaskStatus}] {item.Role}: {OutputTextPreview.CreateSummary(item.Description).Text}");
        Console.WriteLine($"     agent: {agent}");
        Console.WriteLine($"     subscription: {profile}");
        Console.WriteLine($"     ready: {item.CanPrepare}; {OutputTextPreview.CreateTimeline(item.Detail).Text}");
    }

    Console.WriteLine();
}

private static string FormatSubscriptionPlanModelSummary(SubscriptionPlanModelSummaryDto summary)
{
    var complexity = summary.TaskComplexity is null ? string.Empty : $" ({summary.TaskComplexity})";
    var reasoning = string.IsNullOrWhiteSpace(summary.ReasoningEffort) ? string.Empty : $" reasoning {summary.ReasoningEffort}";
    var paid = summary.IsPotentiallyPaidProvider ? " potentially paid" : string.Empty;
    var estimatedPrompt = summary.EstimatedPromptCharacterCount is null ? string.Empty : $", est prompt {summary.EstimatedPromptCharacterCount.Value} chars";
    var noun = summary.ReadyCount == 1 ? "task" : "tasks";
    return $"{summary.ProviderName}/{summary.ModelName}{complexity}{reasoning}{paid}: {summary.ReadyCount} ready {noun}{estimatedPrompt}";
}

public static void PrintAgents(IReadOnlyList<AgentDefinition> agents)
{
    Console.WriteLine("Agents:");
    foreach (var agent in agents.OrderBy(agent => agent.Role))
    {
        var subscription = agent.Subscription is null
            ? "none"
            : $"{agent.Subscription.WorkerProfileName} model={agent.Subscription.ModelAlias ?? "default"} reasoning={agent.Subscription.ReasoningEffort ?? "default"}";
        Console.WriteLine($"  {agent.Role}: {agent.Name} id={agent.Id.Value} execution={agent.ExecutionPolicy} api={agent.Model.ProviderName}/{agent.Model.ModelName} reasoning={agent.Model.ReasoningEffort ?? "default"} subscription={subscription} status={agent.Status}");
    }
}
}


