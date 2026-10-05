namespace Mcg.AgentOrchestrator.Core;

public sealed record FailedRoundCheckpointReceipt(
    string DispatchId,
    string DirtyStateHash,
    IReadOnlyList<string> DirtyPaths);

public enum FailedRoundCheckpointOutcome
{
    Committed,
    CommitFailed
}

public sealed record FailedRoundCheckpointDecision(
    string DispatchId,
    FailedRoundCheckpointOutcome Outcome,
    string? CommitSha,
    string? Diagnostic);
