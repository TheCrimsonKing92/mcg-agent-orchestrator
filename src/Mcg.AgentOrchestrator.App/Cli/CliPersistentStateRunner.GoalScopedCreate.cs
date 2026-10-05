using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static async Task<T> CommitGoalCreationAsync<T>(
        IOrchestratorStateOutboxRepository repository,
        OrchestratorWorkspace workspace,
        GoalSnapshot preparedSnapshot,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(
            bool ShouldSave,
            T Result,
            IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction)
    {
        BacklogItem? sourceItem = null;
        if (preparedSnapshot.SourceBacklogItemId is { } sourceId)
        {
            sourceItem = await new BacklogStore(workspace.BacklogStorePath).GetByIdPrefixAsync(sourceId);
            if (!string.Equals(sourceItem?.Id, sourceId, StringComparison.Ordinal))
                sourceItem = null;
        }
        var scope = GoalCreationLoadScope.ForPreparedGoal(preparedSnapshot, sourceItem);
        return await repository.TransactGoalCreationWithOutboxAsync(scope, transaction);
    }
}
