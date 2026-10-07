namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateHeartbeatContext(
    string? GoalId,
    string Phase,
    string CurrentTarget,
    int? SlotIndex,
    string HeartbeatPath,
    string? StableSlotHeartbeatPath,
    string CommandLine,
    string? BuildEnvironmentRoot)
{
    internal string? RunClass { get; init; }
    internal string? RunId { get; init; }
}
