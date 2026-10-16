namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record BoardSimulationResult(IReadOnlyList<SimulatedGate> Gates,
    IReadOnlyList<SimulatedLanding> Landings, double TotalGateMinutes);
