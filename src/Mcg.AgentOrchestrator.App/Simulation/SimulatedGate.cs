namespace Mcg.AgentOrchestrator.App.Simulation;

internal enum SimulatedGateSource { Recorded, Sampled, CarriedIn }

internal sealed record SimulatedGate(GateRunKind Kind, IReadOnlyList<RecordedGateMember> Members,
    DateTimeOffset BeginAt, DateTimeOffset EndAt, GateRunOutcome Outcome, SimulatedGateSource Source);
