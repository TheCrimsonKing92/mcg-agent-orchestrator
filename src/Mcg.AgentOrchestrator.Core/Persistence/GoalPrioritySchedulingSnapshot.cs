namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalPrioritySchedulingSnapshot(
    string EffectiveLevel,
    string? InheritedFromGoalId = null,
    long GraphRevision = 0,
    IReadOnlyList<string>? PendingSeedGoalIds = null,
    int GateOvertakes = 0,
    int DispatchOvertakes = 0);
