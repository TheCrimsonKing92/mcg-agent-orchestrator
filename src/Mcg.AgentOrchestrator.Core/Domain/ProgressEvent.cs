namespace Mcg.AgentOrchestrator.Core;

public sealed record ProgressEvent(
    GoalId GoalId,
    TaskId? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt,
    TaskRequeueSkippedPayload? RequeueSkipped = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null,
    OperatorIntentAppliedPayload? OperatorIntentApplied = null,
    HumanInputSupersededPayload? HumanInputSuperseded = null,
    ConductorTickOutcomePayload? TickOutcome = null);

public sealed record ConductorTickOutcomePayload(
    string OutcomeKind,
    string LifecycleState,
    string? EscalationKind,
    PolicyDecisionRecord? Decision = null);

public sealed record PolicyDecisionFact(string Name, string Value);

public sealed record PolicyDecisionRecord(
    string Stage,
    string Action,
    int Rung,
    string DiscriminatingEvidence,
    string Reason,
    IReadOnlyList<PolicyDecisionFact> Facts);

public sealed record HumanInputSupersededPayload(
    IReadOnlyList<string> ResolvedStableIds);

public sealed record OperatorIntentAppliedPayload(
    string IntentId,
    string Verb,
    string? TaskId,
    string Actor,
    string Channel,
    string? AuthenticationAssurance,
    OperatorActorKind ActorKind = OperatorActorKind.Human,
    string? DecisionId = null,
    string? Outcome = null,
    RetryRoundKind? RetryRoundKind = null);

public sealed record TaskRequeueSkippedPayload(
    string TaskId,
    string GoalId,
    string DispatchId,
    string BlockingEntity,
    string? TerminalState,
    string Reason,
    string? Detail = null);
