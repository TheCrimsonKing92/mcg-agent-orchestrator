using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerConsoleEpicSource
{
    Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken cancellationToken);
    Task<EpicPlanView> LoadPlanAsync(PortfolioEpic epic, CancellationToken cancellationToken);
}
