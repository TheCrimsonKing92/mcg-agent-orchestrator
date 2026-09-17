namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCapacityRoot(
    string Key,
    IReadOnlySet<string> MemberGoalIds);

internal sealed record ConductorAcceptanceCapacitySnapshot(
    IReadOnlyList<ConductorAcceptanceCapacityRoot> ActiveRoots)
{
    internal int ActiveRootCount => ActiveRoots.Count;
}

internal sealed partial class ConductorDriver
{
    internal ConductorAcceptanceCapacitySnapshot GetActiveAcceptanceCohortCapacity()
    {
        var activeRoots = _cohortGateRuns
            .Where(pair => !pair.Value.Completion.Task.IsCompleted)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ConductorAcceptanceCapacityRoot(pair.Key, pair.Value.MemberGoalIds))
            .ToArray();
        return new ConductorAcceptanceCapacitySnapshot(activeRoots);
    }
}
