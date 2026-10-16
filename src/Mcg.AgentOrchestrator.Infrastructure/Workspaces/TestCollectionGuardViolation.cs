namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TestCollectionGuardViolation(
    string Rule,
    string ClassFullName,
    string? Collection,
    IReadOnlyList<string> BaselineLanes,
    IReadOnlyList<string> ResolvedLanes,
    string RepairHint);
