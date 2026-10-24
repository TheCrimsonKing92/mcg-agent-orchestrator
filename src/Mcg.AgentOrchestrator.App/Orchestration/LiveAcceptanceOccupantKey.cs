using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LiveAcceptanceOccupantKey
{
    internal static string For(GateLoadContextProbe.LiveGateOccupant gate) =>
        !string.IsNullOrWhiteSpace(gate.GoalId) ? $"goal:{gate.GoalId.Trim()}" : gate.Identity;
}
