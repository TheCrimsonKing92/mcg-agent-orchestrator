using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class HealthTextView
{
public static void PrintHealth(OrchestratorHealthReport report)
{
    Console.WriteLine($"Ready: {report.IsReady}");
    Console.WriteLine("Providers:");
    foreach (var provider in report.Providers)
    {
        Console.WriteLine($"  {provider.ProviderName}: configured={provider.IsConfigured} mode={provider.Mode} ({provider.Detail})");
    }

    Console.WriteLine("Agents:");
    foreach (var agent in report.Agents)
    {
        Console.WriteLine($"  {agent.Role}: valid={agent.IsValid} execution={agent.ExecutionPolicy} api={agent.ProviderName}/{agent.ModelName} reasoning={agent.ReasoningEffort ?? "default"} subscription={agent.SubscriptionProfileName ?? "none"} alternate-ready={agent.HasSubscriptionCapableAlternate} alternates={agent.SubscriptionCapableAlternatesDetail} ({agent.Detail})");
    }

    Console.WriteLine("Worker profiles:");
    foreach (var profile in report.WorkerProfiles)
    {
        Console.WriteLine($"  {profile.Name}: executable={profile.Executable} resolvable={profile.IsResolvable} ({profile.Detail})");
    }
}
}
