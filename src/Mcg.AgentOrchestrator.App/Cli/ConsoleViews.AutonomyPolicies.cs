using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintAutonomyPolicies()
    {
        Console.WriteLine($"Default autonomy policy: {AutonomyPolicy.Default.Name}");
        foreach (var policy in AutonomyPolicy.All)
        {
            Console.WriteLine($"Policy {policy.Name}: {policy.Description}");
            Console.WriteLine(
                $"  starts={policy.AllowsDispatchStart}; model={policy.AllowsModelRun}; refresh={policy.AllowsRefresh}; retry={policy.AllowsRetry}; failover={policy.AllowsProviderFailover}; build-test={policy.AllowsBuildTest}; acceptance={policy.AllowsAcceptance}; cleanup={policy.AllowsWorkspaceCleanup}; backlog-log={policy.AllowsBacklogLogEdit}");
        }
    }
}
