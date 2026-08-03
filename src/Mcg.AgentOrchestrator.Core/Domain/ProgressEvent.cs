namespace Mcg.AgentOrchestrator.Core;

public sealed record ProgressEvent(
    GoalId GoalId,
    TaskId? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt,
    TaskRequeueSkippedPayload? RequeueSkipped = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null);

public sealed record TaskRequeueSkippedPayload(
    string TaskId,
    string GoalId,
    string DispatchId,
    string BlockingEntity,
    string? TerminalState,
    string Reason,
    string? Detail = null);
