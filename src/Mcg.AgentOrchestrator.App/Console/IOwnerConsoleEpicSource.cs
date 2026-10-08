using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleEpicSource
{
    Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken);
}
