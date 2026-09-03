namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAcceptanceCapacitySnapshot(
    int ActiveRootCount);

internal sealed partial class ConductorDriver
{
    internal ConductorAcceptanceCapacitySnapshot GetActiveAcceptanceCohortCapacity()
    {
        var activeRootCount = _cohortGateRuns.Count(pair => !pair.Value.Completion.Task.IsCompleted);
        return new ConductorAcceptanceCapacitySnapshot(activeRootCount);
    }
}
