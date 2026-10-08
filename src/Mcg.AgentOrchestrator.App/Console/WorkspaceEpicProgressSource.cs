using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Use the same portfolio, goal metadata and backlog reads as epic-list.
internal sealed class WorkspaceEpicProgressSource(OrchestratorWorkspace workspace) : IOwnerConsoleEpicSource
{
    public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken) =>
        Task.Run(() => EpicProgressReadModel.Load(workspace, since), cancellationToken);
}
