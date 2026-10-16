namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorLiveGroupedGateCount
{
    internal static int Count(ConductorGroupedGateAttemptCoordinator? coordinator) =>
        coordinator?.ReadAll().Count(coordinator.ReservesCapacity) ?? 0;
}
