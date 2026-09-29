namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class TerminalGoalSweep
{
    internal static IReadOnlyList<string> ReapOrphanFixtures(string stateDbPath)
    {
        IReadOnlyList<string>? sharedStores;
        string? failure;
        try
        {
            sharedStores = GetSharedGoalStorePaths(stateDbPath, out failure);
        }
        catch (Exception ex)
        {
            return [$"SWEEP_ORPHAN_FIXTURE_SKIPPED reason=protection-source-unavailable source=shared-store-discovery error={ex.GetType().Name}"];
        }
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
