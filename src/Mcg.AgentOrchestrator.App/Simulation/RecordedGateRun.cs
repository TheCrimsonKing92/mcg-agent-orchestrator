namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record RecordedGateRun(GateRunKind Kind, IReadOnlyList<RecordedGateMember> Members,
    DateTimeOffset BeginAt, DateTimeOffset EndAt, GateRunOutcome Outcome);
