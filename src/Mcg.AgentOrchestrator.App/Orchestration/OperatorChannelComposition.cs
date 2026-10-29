using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorChannelComposition
{
    internal static IOperatorChannel Create(
        OperatorChannelCatalog catalog,
        string stateDirectory) => NullOperatorChannel.Instance;

    internal static Func<string, CancellationToken, Task<long?>> BuildGoalStateVersionReader(string stateDirectory)
    {
        var stateDbPath = Path.Combine(stateDirectory, "state.db");
        return (goalId, cancellationToken) =>
            SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(stateDbPath, goalId, cancellationToken);
    }
}
