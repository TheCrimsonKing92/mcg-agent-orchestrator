using Mcg.AgentOrchestrator.Core;

public sealed class LifecycleEntryPolicyTests
{
    [Xunit.Theory]
    [Xunit.MemberData(nameof(Cases))]
    public void EachRung_PreservesActionEvidenceAndReason_AndReplays(
        LifecycleEntryFacts facts, LifecycleEntryAction action, int rung, string evidence, string reason)
    {
        AssertDecision(LifecycleEntryPolicy.Evaluate(facts), action, rung, evidence, reason);
        var recorded = facts.ToRecordedFacts();
        Assert.Equal(7, recorded.Count);
        Assert.Equal(new[] { "resolved-state", "slice-batch-parent-hold", "state-is-failed",
            "awaiting-clarification-reason", "terminal-escalation-reason", "policy-name", "transition-decision" },
            recorded.Select(fact => fact.Name));
        AssertDecision(LifecycleEntryPolicy.Evaluate(LifecycleEntryFacts.FromRecordedFacts(recorded)), action, rung, evidence, reason);
    }

    public static IEnumerable<object[]> Cases()
    {
        var ready = new LifecycleEntryFacts(GoalLifecycleState.WorkspaceReady)
        {
            StateIsFailed = false, PolicyName = "Manual", TransitionDecision = "Escalate"
        };
        const string hold = "Slice-batch parent abcdef12 owns child goals and does not execute worker tasks.";
        yield return [ready with { SliceBatchParentHold = hold }, LifecycleEntryAction.Hold, 1, "slice-batch-parent-hold", hold];
        yield return [ready with { ResolvedState = GoalLifecycleState.Failed, StateIsFailed = true },
            LifecycleEntryAction.Proceed, 2, "failed-recovery", "Failed goal recovery owns this state."];
        yield return [ready with { ResolvedState = GoalLifecycleState.AwaitingClarification, AwaitingClarificationReason = "injected clarification reason" },
            LifecycleEntryAction.Escalate, 3, "awaiting-clarification", "injected clarification reason"];
        yield return [ready with { ResolvedState = GoalLifecycleState.AwaitingClarification },
            LifecycleEntryAction.Escalate, 3, "awaiting-clarification", "Goal is in AwaitingClarification state; operator action required"];
        yield return [ready with { ResolvedState = GoalLifecycleState.Blocked,
            TerminalEscalationReason = "Goal is in Blocked state; operator action required" },
            LifecycleEntryAction.Escalate, 4, "terminal-escalation", "Goal is in Blocked state; operator action required"];
        yield return [ready with { ResolvedState = GoalLifecycleState.AwaitingHumanInput,
            TerminalEscalationReason = "Goal is in AwaitingHumanInput state; operator action required" },
            LifecycleEntryAction.Escalate, 4, "terminal-escalation", "Goal is in AwaitingHumanInput state; operator action required"];
        yield return [ready, LifecycleEntryAction.Escalate, 5, "policy-manual-review", "Policy 'Manual' requires manual review at WorkspaceReady"];
        yield return [ready with { PolicyName = "Conservative", TransitionDecision = "Auto" },
            LifecycleEntryAction.Proceed, 0, "lifecycle-entry-proceed", "Lifecycle entry may proceed."];
        yield return [new LifecycleEntryFacts(GoalLifecycleState.Merged) { StateIsFailed = false, PolicyName = "Manual" },
            LifecycleEntryAction.Proceed, 0, "lifecycle-entry-proceed", "Lifecycle entry may proceed."];
    }

    [Xunit.Theory]
    [Xunit.InlineData("true", true)]
    [Xunit.InlineData("false", false)]
    [Xunit.InlineData("", null)]
    public void BooleanFact_RoundTripsTrueFalseAndUnread(string encoded, bool? value)
    {
        var facts = new LifecycleEntryFacts(GoalLifecycleState.Created) { StateIsFailed = value };
        Assert.Equal(encoded, Assert.Single(facts.ToRecordedFacts(), item => item.Name == "state-is-failed").Value);
        Assert.Equal(value, LifecycleEntryFacts.FromRecordedFacts(facts.ToRecordedFacts()).StateIsFailed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("count")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("state")]
    [Xunit.InlineData("boolean")]
    [Xunit.InlineData("transition")]
    [Xunit.InlineData("null-value")]
    public void Replay_RejectsMalformedFacts(string defect)
    {
        var facts = new LifecycleEntryFacts(GoalLifecycleState.Created).ToRecordedFacts().ToList();
        switch (defect)
        {
            case "count": facts.RemoveAt(0); break;
            case "duplicate": facts[1] = facts[0]; break;
            case "missing": facts[1] = new("unknown", ""); break;
            case "state": facts[0] = new("resolved-state", "0"); break;
            case "boolean": facts[2] = new("state-is-failed", "False"); break;
            case "transition": facts[6] = new("transition-decision", "Unknown"); break;
            case "null-value": facts[1] = new("slice-batch-parent-hold", null!); break;
        }
        Assert.Throws<InvalidOperationException>(() => LifecycleEntryFacts.FromRecordedFacts(facts));
    }

    [Xunit.Fact]
    public void TerminalState_WithoutReason_FailsLoudly() =>
        Assert.Throws<InvalidOperationException>(() => LifecycleEntryPolicy.Evaluate(new(GoalLifecycleState.Blocked)));

    [Xunit.Fact]
    public void NullInput_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => LifecycleEntryPolicy.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => LifecycleEntryFacts.FromRecordedFacts(null!));
    }

    private static void AssertDecision(LifecycleEntryDecision actual, LifecycleEntryAction action, int rung, string evidence, string reason)
    {
        Assert.Equal(action, actual.Action);
        Assert.Equal(rung, actual.DiscriminatingRung);
        Assert.Equal(evidence, actual.DiscriminatingEvidence);
        Assert.Equal(reason, actual.Reason);
        var record = actual.ToRecord();
        Assert.Equal("lifecycle-entry", record.Stage);
        Assert.Equal(action.ToString(), record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
    }
}
