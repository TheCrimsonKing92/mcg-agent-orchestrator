using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class EmptyOwnerConsoleStateQueries : IOrchestratorStateQueries
{
    public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GoalSummary>>([]);

    public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentOrchestratorKernel());
}
