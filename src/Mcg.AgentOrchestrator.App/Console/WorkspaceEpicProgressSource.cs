using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Use the same portfolio, goal metadata and backlog reads as epic-list.
internal sealed class WorkspaceEpicProgressSource(OrchestratorWorkspace workspace,
    Action<string, SqliteConnection>? observeDetailRead = null) : IOwnerConsoleEpicSource
{
    public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken) =>
        Task.Run(() => EpicProgressReadModel.Load(workspace, since, includeReasons: true), cancellationToken);

    public Task<EpicDetailReadSnapshot> LoadDetailAsync(string epicId, DateTimeOffset? since, CancellationToken cancellationToken) =>
        Task.Run(() => EpicDetailReadSnapshot.ReadAsync(workspace, epicId, since, cancellationToken, observeDetailRead), cancellationToken);

    public async Task<string?> FindGoalEpicAsync(string goalId, CancellationToken cancellationToken)
    {
        if (!File.Exists(workspace.PortfolioStorePath)) return null;
        return (await PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath)
            .GetGoalMembershipAsync(goalId, cancellationToken).ConfigureAwait(false))?.EpicId;
    }
}
