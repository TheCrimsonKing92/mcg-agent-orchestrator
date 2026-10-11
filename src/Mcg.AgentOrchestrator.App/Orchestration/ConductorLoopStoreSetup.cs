using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorLoopStoreSetup
{
    public static IReadOnlyList<StoreSetupResult> Run(
        string orchestratorDirectory,
        Func<string, IReadOnlyList<StoreSetupResult>>? storeSetup = null)
    {
        var results = (storeSetup ?? StoreSetupRunner.Run)(orchestratorDirectory);
        foreach (var result in results)
        {
            var entry = StoreSchemaRegistry.Inventory.FirstOrDefault(
                entry => string.Equals(entry.StoreName, result.StoreName, StringComparison.Ordinal));
            if (entry?.CurrentVersion is { } expectedVersion && result.Version > expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Store '{result.StoreName}' at '{result.DatabasePath}' records schema version {result.Version}, " +
                    $"newer than expected version {expectedVersion}; upgrade the orchestrator before starting the conductor loop.");
            }
        }

        return results;
    }
}
