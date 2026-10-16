namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record BoardHistoryTrace(DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    IReadOnlyList<RecordedGateRun> GateRuns, IReadOnlyList<RecordedReadinessEpoch> Epochs,
    IReadOnlyList<RecordedLanding> Landings, IReadOnlyList<SkippedTraceInput> Skipped);
