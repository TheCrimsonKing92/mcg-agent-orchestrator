namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class TerminalGoalSweep
{
    private static IReadOnlyList<string> ReapOrphanFixtures(string stateDbPath)
    {
        var sharedStores = GetSharedGoalStorePaths(stateDbPath, out var failure);
        if (sharedStores is null)
            return failure is null ? [] :
                ["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=protection-source-unavailable source=shared-store-discovery"];
        try
        {
            var directory = Path.GetDirectoryName(stateDbPath)!;
            return new OrphanFixtureReaper(
                new SystemOrphanFixtureReaperSources(directory, sharedStores), TimeProvider.System).RunPass();
        }
        catch (Exception ex)
        {
            return [$"SWEEP_ORPHAN_FIXTURE_SKIPPED reason=reaper-failed error={ex.GetType().Name}"];
        }
    }
}
