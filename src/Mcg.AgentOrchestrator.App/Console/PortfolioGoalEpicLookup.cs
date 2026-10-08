using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class PortfolioGoalEpicLookup(string path) : IOwnerGoalEpicLookup
{
    private PortfolioStore? _store;

    public async Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            _store ??= new PortfolioStore(path);
            return (await _store.GetGoalMembershipAsync(goalId, cancellationToken))?.EpicTitle ?? string.Empty;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
