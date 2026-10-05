namespace Mcg.AgentOrchestrator.Core;

public enum LifecycleEntryAction
{
    Proceed,
    Hold,
    Escalate
}

/// <summary>Observed lifecycle-entry inputs; empty values denote inputs not read on this path.</summary>
public sealed record LifecycleEntryFacts(GoalLifecycleState ResolvedState)
{
    public string SliceBatchParentHold { get; init; } = string.Empty;
    public bool? StateIsFailed { get; init; }
    public string AwaitingClarificationReason { get; init; } = string.Empty;
    public string TerminalEscalationReason { get; init; } = string.Empty;
    public string PolicyName { get; init; } = string.Empty;
    public string TransitionDecision { get; init; } = string.Empty;

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("resolved-state", ResolvedState.ToString()),
        new("slice-batch-parent-hold", SliceBatchParentHold),
        new("state-is-failed", StateIsFailed switch { true => "true", false => "false", null => "" }),
        new("awaiting-clarification-reason", AwaitingClarificationReason),
        new("terminal-escalation-reason", TerminalEscalationReason),
        new("policy-name", PolicyName),
        new("transition-decision", TransitionDecision)
    ]);

    public static LifecycleEntryFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 7)
            throw new InvalidOperationException("Lifecycle entry replay requires exactly seven named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Invalid or duplicate lifecycle entry fact '{fact.Name}'.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing lifecycle entry fact '{name}'.");
        var stateText = Read("resolved-state");
        if (!Enum.TryParse<GoalLifecycleState>(stateText, out var state) ||
            !Enum.IsDefined(state) || state.ToString() != stateText)
            throw new InvalidOperationException("Invalid lifecycle entry resolved-state fact.");
        bool? failed = Read("state-is-failed") switch
        {
            "true" => true,
            "false" => false,
            "" => null,
            _ => throw new InvalidOperationException("Invalid lifecycle entry state-is-failed fact.")
        };
        var transition = Read("transition-decision");
        if (transition is not ("" or "Auto" or "Escalate"))
            throw new InvalidOperationException("Invalid lifecycle entry transition-decision fact.");

        return new(state)
        {
            SliceBatchParentHold = Read("slice-batch-parent-hold"),
            StateIsFailed = failed,
            AwaitingClarificationReason = Read("awaiting-clarification-reason"),
            TerminalEscalationReason = Read("terminal-escalation-reason"),
            PolicyName = Read("policy-name"),
            TransitionDecision = transition
        };
    }
}

public sealed record LifecycleEntryDecision(
    LifecycleEntryAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    LifecycleEntryFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        LifecycleEntryPolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects lifecycle-entry routing from observed facts without owning effects.</summary>
public static class LifecycleEntryPolicy
{
    public const string StageName = "lifecycle-entry";

    public static LifecycleEntryDecision Evaluate(LifecycleEntryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.SliceBatchParentHold.Length > 0)
            return new(LifecycleEntryAction.Hold, 1, "slice-batch-parent-hold", facts.SliceBatchParentHold, facts);
        if (facts.StateIsFailed == true)
            return new(LifecycleEntryAction.Proceed, 2, "failed-recovery", "Failed goal recovery owns this state.", facts);
        if (facts.ResolvedState == GoalLifecycleState.AwaitingClarification)
            return new(LifecycleEntryAction.Escalate, 3, "awaiting-clarification",
                facts.AwaitingClarificationReason.Length > 0 ? facts.AwaitingClarificationReason :
                $"Goal is in {facts.ResolvedState} state; operator action required", facts);
        if (facts.ResolvedState is GoalLifecycleState.Blocked or GoalLifecycleState.AwaitingHumanInput)
        {
            if (facts.TerminalEscalationReason.Length == 0)
                throw new InvalidOperationException("Terminal lifecycle entry requires an escalation reason.");
            return new(LifecycleEntryAction.Escalate, 4, "terminal-escalation", facts.TerminalEscalationReason, facts);
        }
        if (facts.TransitionDecision == "Escalate")
            return new(LifecycleEntryAction.Escalate, 5, "policy-manual-review",
                $"Policy '{facts.PolicyName}' requires manual review at {facts.ResolvedState}", facts);

        return new(LifecycleEntryAction.Proceed, 0, "lifecycle-entry-proceed", "Lifecycle entry may proceed.", facts);
    }
}
