using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Typed evidence for what one automated advance step actually produced. This replaces the untyped
/// <c>object?</c> result the presentation DTO used to carry, so a non-presentation consumer can
/// branch on the outcome without pattern-matching a transport type.
/// </summary>
public abstract record GoalAdvanceStepPayload;

public sealed record GoalAdvanceNoPayload : GoalAdvanceStepPayload;

public sealed record GoalAdvanceDispatchPrepared(WorkerProfileDispatchResult Dispatch) : GoalAdvanceStepPayload;

public sealed record GoalAdvanceTaskUpdated(TaskSpec Task) : GoalAdvanceStepPayload;

public sealed record GoalAdvanceDelegationPlanned(DelegationPlan Plan) : GoalAdvanceStepPayload;

public sealed record GoalAdvanceDispatchStartFailed(DispatchProcessStartFailure Failure) : GoalAdvanceStepPayload;

/// <param name="ActionDispatchState">
/// Authoritative dispatch state for <paramref name="Action"/> as it stood immediately before this
/// step executed. Execution mutates the live goal - refreshing a running process sets
/// <c>CompletedAt</c>, which makes a later evaluation return null - so an adapter that rendered the
/// action after the step would describe a different action than the operation actually chose.
/// </param>
public sealed record GoalAdvanceOutcome(
    GoalId GoalId,
    bool Executed,
    NextActionItem? Action,
    NextActionAutomationKind AutomationKind,
    string Message,
    GoalAdvanceStepPayload Payload,
    bool StateChanged = false,
    DispatchAuthoritativeState? ActionDispatchState = null);

/// <param name="BlockingActionDispatchState">
/// Pre-execution dispatch state for <paramref name="BlockingAction"/>, captured on the iteration
/// that stopped the loop. See <see cref="GoalAdvanceOutcome.ActionDispatchState"/>.
/// </param>
public sealed record GoalAdvanceLoopOutcome(
    GoalId GoalId,
    bool Executed,
    int StepCount,
    string StopReason,
    NextActionItem? BlockingAction,
    IReadOnlyList<GoalAdvanceOutcome> Steps,
    DateTimeOffset? ContinueAfter = null,
    bool StateChanged = false,
    DispatchProcessStartFailure? Failure = null,
    DispatchAuthoritativeState? BlockingActionDispatchState = null);
