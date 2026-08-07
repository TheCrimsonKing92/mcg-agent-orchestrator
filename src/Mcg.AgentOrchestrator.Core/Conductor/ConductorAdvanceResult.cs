using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Conductor;

public abstract record ConductorAdvanceOutcome
{
    public sealed record Executed(GoalLifecycleState FromState, string Description) : ConductorAdvanceOutcome;
    public sealed record Held(GoalLifecycleState State, string Reason, string? StableIdentity = null) : ConductorAdvanceOutcome;
    public sealed record Escalated(GoalLifecycleState State, string Reason) : ConductorAdvanceOutcome;
    public sealed record Done(GoalLifecycleState State) : ConductorAdvanceOutcome;
}

public sealed record ConductorAdvanceResult(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    ConductorAdvanceOutcome Outcome)
{
    public bool WasEscalated => Outcome is ConductorAdvanceOutcome.Escalated;
    public bool WasExecuted => Outcome is ConductorAdvanceOutcome.Executed;
    public bool IsDone => Outcome is ConductorAdvanceOutcome.Done;
    public bool IsHeld => Outcome is ConductorAdvanceOutcome.Held;
}
