using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalPortfolioMembershipReader
{
    public static PortfolioMembership? Read(string path, string goalId)
    {
        // Reading a receipt or served status must not create or upgrade a portfolio store.
        if (!File.Exists(path)) return null;
        PortfolioStore store;
        try { store = PortfolioStore.OpenReadOnly(path); }
        catch (InvalidOperationException) { return null; } // Open documents missing/non-current schema.
        catch (SqliteException ex) when (ex.SqliteErrorCode == 14 && !File.Exists(path)) { return null; }
        return store.GetGoalMembershipAsync(goalId).GetAwaiter().GetResult();
    }
}
