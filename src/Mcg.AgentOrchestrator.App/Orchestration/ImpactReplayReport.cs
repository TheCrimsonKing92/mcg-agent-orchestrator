namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayReport(
    string FloorSource,
    IReadOnlyList<string> FloorLanes,
    IReadOnlyList<ImpactReplayAttemptRecord> Attempts,
    IReadOnlyList<ImpactReplayDiagnostic> SkippedInputs,
    IReadOnlyList<ImpactReplayDiagnostic> UnreplayableAttempts,
    IReadOnlyList<string> CleanupErrors);
