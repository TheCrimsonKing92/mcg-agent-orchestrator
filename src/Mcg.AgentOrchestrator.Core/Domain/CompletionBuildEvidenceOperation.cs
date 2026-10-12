namespace Mcg.AgentOrchestrator.Core;

public enum CompletionBuildEvidencePhase
{
    Launching, Pending, Success, CompilerFailure, ApparatusFailure, TimedOut, Cancelled, StaleCandidate,
    WaitingForLease
}

// Certifies one dispatch's exact candidate; it is neither a worker nor a product-test verdict.
public sealed record CompletionBuildEvidenceOperation(
    string Id,
    string GoalId,
    string TaskId,
    DateTimeOffset DispatchAt,
    string DispatchId,
    string WorktreeRoot,
    string TreeDigest,
    IReadOnlyList<string> Projects,
    TaskProcessRecord WorkerProcess,
    DateTimeOffset Deadline,
    CompletionBuildEvidencePhase Phase,
    string StatePath,
    string ExecutionDirectory,
    string ResultPath,
    int LaunchOwnerPid,
    DateTimeOffset LaunchOwnerStartedAt,
    int? ChildPid = null,
    DateTimeOffset? ChildStartedAt = null,
    string? ChildImagePath = null,
    string? LeaseOwner = null,
    int? BuildExitCode = null,
    string Diagnostic = "",
    bool Applied = false)
{
    public bool IsPending => Phase is CompletionBuildEvidencePhase.Launching or CompletionBuildEvidencePhase.Pending or CompletionBuildEvidencePhase.WaitingForLease;
}
