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
    bool Applied = false,
    bool CustodyReleased = false)
{
    public bool IsPending => Phase is CompletionBuildEvidencePhase.Launching or CompletionBuildEvidencePhase.Pending or CompletionBuildEvidencePhase.WaitingForLease;

    // Stable authority for the original dispatch, independent of its current-task applicability.
    public bool HasSameOrigin(CompletionBuildEvidenceOperation other) =>
        Id == other.Id && GoalId == other.GoalId && TaskId == other.TaskId &&
        DispatchAt == other.DispatchAt && DispatchId == other.DispatchId &&
        WorktreeRoot == other.WorktreeRoot && TreeDigest == other.TreeDigest &&
        StatePath == other.StatePath && ExecutionDirectory == other.ExecutionDirectory &&
        ResultPath == other.ResultPath && Deadline == other.Deadline &&
        LaunchOwnerPid == other.LaunchOwnerPid && LaunchOwnerStartedAt == other.LaunchOwnerStartedAt &&
        WorkerProcess.ProcessId == other.WorkerProcess.ProcessId && WorkerProcess.StartedAt == other.WorkerProcess.StartedAt &&
        WorkerProcess.Command == other.WorkerProcess.Command && WorkerProcess.WorkingDirectory == other.WorkerProcess.WorkingDirectory &&
        WorkerProcess.CompletedAt == other.WorkerProcess.CompletedAt && WorkerProcess.ExitCode == other.WorkerProcess.ExitCode &&
        WorkerProcess.StandardOutputPath == other.WorkerProcess.StandardOutputPath &&
        WorkerProcess.StandardErrorPath == other.WorkerProcess.StandardErrorPath && WorkerProcess.ExitCodePath == other.WorkerProcess.ExitCodePath &&
        Projects.SequenceEqual(other.Projects);
}

// An attributed operation selector binds the child and receipt that the operator observed.
public sealed record CompletionBuildEvidenceTarget(
    string OperationId, string DispatchId, string TreeDigest, string ResultPath,
    int? ChildPid, DateTimeOffset? ChildStartedAt, string? ChildImagePath, string? LeaseOwner)
{
    public static CompletionBuildEvidenceTarget From(CompletionBuildEvidenceOperation operation) =>
        new(operation.Id, operation.DispatchId, operation.TreeDigest, operation.ResultPath,
            operation.ChildPid, operation.ChildStartedAt, operation.ChildImagePath, operation.LeaseOwner);

    public bool Matches(CompletionBuildEvidenceOperation operation) =>
        this == From(operation);
}
