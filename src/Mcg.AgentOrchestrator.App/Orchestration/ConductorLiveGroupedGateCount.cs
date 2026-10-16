namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorLiveGroupedGateCount
{
    internal static int Count(ConductorGroupedGateAttemptCoordinator? coordinator) =>
        coordinator?.ReadAll().Count(attempt =>
            attempt.ReconciledAt is null && attempt.Outcome == "Running" &&
            !File.Exists(attempt.ResultPath) && !File.Exists(attempt.ExitCodePath) &&
            coordinator.IsAlive(attempt.OwnerProcessId)) ?? 0;
}
