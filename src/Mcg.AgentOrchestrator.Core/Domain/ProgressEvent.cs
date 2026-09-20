namespace Mcg.AgentOrchestrator.Core;

public sealed record ProgressEvent(
    GoalId GoalId,
    TaskId? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt,
    TaskRequeueSkippedPayload? RequeueSkipped = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null,
    OperatorIntentAppliedPayload? OperatorIntentApplied = null);

public sealed record OperatorIntentAppliedPayload(
    string IntentId,
    string Verb,
    string? TaskId,
    string Actor,
    string Channel,
    string? AuthenticationAssurance);

public sealed record TaskRequeueSkippedPayload(
    string TaskId,
    string GoalId,
    string DispatchId,
    string BlockingEntity,
    string? TerminalState,
    string Reason,
    string? Detail = null);
