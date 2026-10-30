using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Resolves only the recorded owner PID; command markers are identity checks, never selectors.
internal sealed class InvalidatedAcceptanceAttemptProcessStop
{
    private readonly Func<int, ProcessInspectionRecord?> _inspect;
    private readonly Func<int, IReadOnlyList<int>> _descendants;
    private readonly Func<int, bool> _stop;

    internal InvalidatedAcceptanceAttemptProcessStop(
        Func<int, ProcessInspectionRecord?>? inspect = null,
        Func<int, IReadOnlyList<int>>? descendants = null,
        Func<int, bool>? stop = null)
    {
        _inspect = inspect ?? (pid => ProcessCommandLines.Snapshot([pid]).Records.GetValueOrDefault(pid));
        _descendants = descendants ?? WorkerProcessJobs.ListLiveDescendantProcessIds;
        _stop = stop ?? WorkerProcessJobs.TryKillOrFallback;
    }

    internal InvalidatedAcceptanceAttemptStopResult Stop(ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.OwnerProcessId <= 0 || attempt.OwnerProcessStartedAt is null ||
            string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath))
            return new("refused-identity", [], "identity-unavailable");

        var current = _inspect(attempt.OwnerProcessId);
        if (current is null) return new("already-exited", []);
        var expected = current with
        {
            StartedAt = attempt.OwnerProcessStartedAt,
            ExecutablePath = attempt.OwnerExecutablePath
        };
        var markers = new[] { ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName, attempt.MetadataPath };
        if (ProcessStopRevalidation.Evaluate(expected, current, markers) is { } refusal)
            return new("refused-identity", [], refusal);

        var descendants = _descendants(attempt.OwnerProcessId);
        // Reinspect immediately before the consequential action, including after the tree snapshot.
        current = _inspect(attempt.OwnerProcessId);
        if (current is null) return new("already-exited", []);
        if (ProcessStopRevalidation.Evaluate(expected, current, markers) is { } changed)
            return new("refused-identity", [], changed);

        return new(_stop(attempt.OwnerProcessId) ? "stopped" : "failed", descendants);
    }
}
