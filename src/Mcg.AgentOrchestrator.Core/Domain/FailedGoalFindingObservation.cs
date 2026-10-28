using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

public sealed record FailedGoalPendingNote(TaskId TaskId, string Message);

public sealed record FailedGoalFindingObservation(
    FailedGoalFindingObservationKind Kind,
    TaskId? TargetTaskId,
    string? AttemptIdentity,
    string Evidence,
    RetryRoundKind? RoundKind = null,
    RetryCause? ObservedCause = null,
    string? WarningMessage = null,
    string Attribution = "structured-finding-routing",
    ImmutableArray<FailedGoalPendingNote> PendingNotes = default, Conductor.ConductorHoldOwner HoldOwner = default,
    FindingEvidenceReviewContinuation? EvidenceContinuation = null)
{
    public static FailedGoalFindingObservation None { get; } = new(
        FailedGoalFindingObservationKind.None,
        null,
        null,
        string.Empty);

    public static FailedGoalFindingObservation Observed(
        FailedGoalFindingObservationKind kind,
        string evidence) => new(
        kind,
        null,
        null,
        evidence);

    public static FailedGoalFindingObservation Routed(
        FailedGoalFindingObservationKind kind,
        TaskId targetTaskId,
        string attemptIdentity,
        string evidence,
        string? warningMessage,
        RetryRoundKind? roundKind = null,
        RetryCause? observedCause = null) => new(
            kind,
            targetTaskId,
            attemptIdentity,
            evidence,
            roundKind,
            observedCause,
            warningMessage);
    public static string BuildAttemptIdentity(TaskSpec task) => task.LastProcess is { } process
        ? $"process:{process.ProcessId}:{process.StartedAt.UtcTicks}"
        : task.LastVerification is { } verification
            ? $"verification:{verification.CompletedAt.UtcTicks}:{verification.ChildProcessId?.ToString() ?? "none"}"
            : "unobserved-attempt";
}
