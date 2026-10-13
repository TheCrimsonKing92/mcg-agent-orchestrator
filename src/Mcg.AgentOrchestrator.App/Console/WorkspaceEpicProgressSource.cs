using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Use the same portfolio, goal metadata and backlog reads as epic-list.
internal sealed class WorkspaceEpicProgressSource(OrchestratorWorkspace workspace) : IOwnerConsoleEpicSource
{
    public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken) =>
        Task.Run(() => EpicProgressReadModel.Load(workspace, since, includeReasons: true), cancellationToken);

    public Task<EpicPlanView> LoadPlanAsync(PortfolioEpic epic, CancellationToken cancellationToken) =>
        Task.Run(() => EpicProgressReadModel.LoadEpic(workspace, epic.Id, includeReasons: true).Plan!, cancellationToken);
}
