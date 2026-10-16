namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    internal IReadOnlyList<string> GetRunningAttemptExitCodePaths(IEnumerable<string> goalIds) =>
        GetUnreconciledAttempts(goalIds)
            .Where(IsCapacityReservingAttempt)
            .Select(attempt => attempt.ExitCodePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
