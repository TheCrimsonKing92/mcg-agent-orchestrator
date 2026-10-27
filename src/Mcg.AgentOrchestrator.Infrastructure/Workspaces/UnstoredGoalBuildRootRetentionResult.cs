namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record UnstoredGoalBuildRootRetentionResult(
    int InspectedCount,
    int ReclaimedCount,
    long BytesReclaimed,
    IReadOnlyDictionary<UnstoredGoalBuildRootKeepReason, int> Kept,
    IReadOnlyList<string> ReclaimedRoots,
    string? NextCursor,
    string SummaryLine);
