using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class WorkspaceConfigurationTextView
{
    public static void PrintTenant(OrchestratorWorkspace workspace)
    {
        Console.WriteLine($"Tenant: {workspace.TenantName}");
        Console.WriteLine($"Tenant scoped: {workspace.IsTenantScoped}");
        Console.WriteLine($"State: {workspace.SqliteStatePath}");
        Console.WriteLine($"Agents: {workspace.AgentCatalogPath}");
        Console.WriteLine($"Workers: {workspace.WorkerProfilePath}");
        Console.WriteLine($"Continuations: {workspace.ContinuationStorePath}");
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
}
