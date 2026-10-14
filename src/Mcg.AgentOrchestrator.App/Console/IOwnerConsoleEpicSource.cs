using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleEpicSource
{
    Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken);
    Task<EpicDetailReadSnapshot> LoadDetailAsync(string epicId, DateTimeOffset? since, CancellationToken cancellationToken);
    Task<string?> FindGoalEpicAsync(string goalId, CancellationToken cancellationToken);
}
