using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class EmptyOwnerConsoleStateQueries : IOrchestratorStateQueries
{
    public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]);

    public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
        IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);

    public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GoalSummary>>([]);

    public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentOrchestratorKernel());
}
