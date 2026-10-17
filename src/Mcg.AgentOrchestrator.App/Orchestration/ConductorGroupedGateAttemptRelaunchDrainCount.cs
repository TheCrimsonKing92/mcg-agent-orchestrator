namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorGroupedGateAttemptRelaunchDrainCount
{
    internal static int Count(ConductorGroupedGateAttemptCoordinator? coordinator) =>
        // Durable holders survive handoff; do not let the drain re-adopt their leases.
        coordinator?.ReadAll().Count(attempt =>
            !(attempt.HolderContractVersion == 1 &&
              File.Exists(AcceptanceHolderBinding.For(attempt).LeasePath)) &&
            coordinator.ReservesCapacity(attempt)) ?? 0;
}
