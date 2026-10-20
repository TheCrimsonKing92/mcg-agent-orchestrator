namespace Mcg.AgentOrchestrator.Core;

// A host observation of one live worker request. This is not completion evidence or a test verdict.
public enum WorkerBuildPhase
{
    Requested, Authorized, WaitingForCustody, CustodyAcquired, WaitingForBuildSlot, PreparingLaunch,
    ChildRegistered, CompilerStarted, Completed,
    // Append to preserve numeric values in existing durable observations.
    BuildSlotAcquired
}

public enum WorkerBuildOutcome
{
    Success, CompilerFailure, CapacityBeforeStart, TimeoutAfterStart, Cancelled,
    StaleCandidate, Interrupted, ApparatusFailure
}

public sealed record WorkerBuildProcess(int Pid, DateTimeOffset StartedAt, string ImagePath);
public sealed record WorkerBuildObservation(WorkerBuildPhase Phase, DateTimeOffset At);

public sealed record WorkerBuildInvocation(
    string Id, string RequestKey, string GoalId, string TaskId, DateTimeOffset DispatchAt,
    string DispatchId, string WorktreeRoot, string TreeDigest, string ScopeIdentity,
    IReadOnlyList<string> Projects, string Configuration, DateTimeOffset Deadline,
    WorkerBuildProcess Host, WorkerBuildProcess Worker, string AdmissionStorePath, long AdmissionVersion, int Version,
    IReadOnlyList<WorkerBuildObservation> Observations,
    IReadOnlyList<WorkerBuildProcess>? Children = null, string? LeaseOwner = null,
    WorkerBuildOutcome? Outcome = null, int? CompilerExitCode = null,
    bool Consumed = false, bool CustodyReleased = false, string Diagnostic = "")
{
    public bool IsTerminal => Outcome is not null;
    public WorkerBuildPhase Phase => Observations[^1].Phase;

    public bool SameOrigin(WorkerBuildInvocation other) => Id == other.Id && RequestKey == other.RequestKey &&
        GoalId == other.GoalId && TaskId == other.TaskId && DispatchAt == other.DispatchAt &&
        DispatchId == other.DispatchId && WorktreeRoot == other.WorktreeRoot && TreeDigest == other.TreeDigest &&
        ScopeIdentity == other.ScopeIdentity && Projects.SequenceEqual(other.Projects) &&
        Configuration == other.Configuration && Deadline == other.Deadline && Host == other.Host && Worker == other.Worker &&
        AdmissionStorePath == other.AdmissionStorePath && AdmissionVersion == other.AdmissionVersion;

    public void RequireSuccessor(WorkerBuildInvocation next)
    {
        next.RequireValid();
        if (!SameOrigin(next) || next.Version != Version + 1 ||
            Observations.Count > next.Observations.Count ||
            !Observations.SequenceEqual(next.Observations.Take(Observations.Count)) ||
            !(Children ?? []).SequenceEqual((next.Children ?? []).Take(Children?.Count ?? 0)) ||
            (Children?.Count ?? 0) > (next.Children?.Count ?? 0) ||
            LeaseOwner is not null && LeaseOwner != next.LeaseOwner ||
            Outcome is not null && (Outcome != next.Outcome || CompilerExitCode != next.CompilerExitCode) ||
            Consumed && !next.Consumed || CustodyReleased && !next.CustodyReleased ||
            next.Consumed && !next.IsTerminal)
            throw new InvalidOperationException("Worker build invocation cannot replace identity or move backwards.");
    }

    public void RequireValid()
    {
        var started = Observations.Count(o => o.Phase == WorkerBuildPhase.CompilerStarted);
        var children = Children?.Count ?? 0;
        if (Observations.Count == 0 || Projects.Count == 0 || string.IsNullOrWhiteSpace(TreeDigest) ||
            string.IsNullOrWhiteSpace(ScopeIdentity) || children > Projects.Count || started > children ||
            (Phase == WorkerBuildPhase.Completed) != IsTerminal || CustodyReleased && !IsTerminal ||
            Consumed && (!IsTerminal || !CustodyReleased) ||
            Outcome == WorkerBuildOutcome.Success && (children != Projects.Count || started != children || CompilerExitCode != 0) ||
            Outcome == WorkerBuildOutcome.CompilerFailure && (started == 0 || CompilerExitCode is null or 0) ||
            Outcome == WorkerBuildOutcome.TimeoutAfterStart && started == 0 ||
            Outcome == WorkerBuildOutcome.CapacityBeforeStart && (children != 0 || started != 0 ||
                Observations.Any(o => o.Phase == WorkerBuildPhase.BuildSlotAcquired) ||
                !Observations.Any(o => o.Phase == WorkerBuildPhase.WaitingForBuildSlot)))
            throw new InvalidOperationException("Worker build record lacks observations required by its outcome.");
    }
}
