namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TerminalBuildRootRetentionResult(
    int ReclaimedCount,
    long BytesReclaimed,
    IReadOnlyDictionary<TerminalBuildRootSkipReason, int> Skipped,
    IReadOnlyList<string> ReclaimedRoots,
    string SummaryLine);
