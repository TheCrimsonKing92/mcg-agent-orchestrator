namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCapacityRoot(
    string Key,
    IReadOnlySet<string> MemberGoalIds,
    DateTimeOffset StartedAt = default);

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
            .Select(pair => new ConductorAcceptanceCapacityRoot(
                pair.Key,
                pair.Value.MemberGoalIds,
                pair.Value.StartedAt))
            .ToArray();
        return new ConductorAcceptanceCapacitySnapshot(activeRoots);
    }
}
