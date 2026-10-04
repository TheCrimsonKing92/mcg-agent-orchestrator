using Mcg.AgentOrchestrator.Core;

public sealed class VerifyingStagePolicyTests
{
    [Xunit.Theory]
    [Xunit.MemberData(nameof(HoldCases))]
    public void EachHold_PreservesReasonAndDiscriminatingEvidence(
        VerifyingStageFacts facts, int rung, string evidence, string reason)
    {
        var decision = VerifyingStagePolicy.Evaluate(facts);

        Assert.Equal(VerifyingStageAction.Hold, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
    }

    public static IEnumerable<object[]> HoldCases()
    {
        var verifying = new VerifyingStageFacts(GoalLifecycleState.Verifying, true, GoalStatus.Verifying);
        var verified = new VerifyingStageFacts(GoalLifecycleState.Verified, true, GoalStatus.Verified,
            ParallelAcceptanceEnabled: true, CandidateBuilt: true, ReplacementLeaseAvailable: true);
        yield return [verifying with { IsConductorTick = false }, 1, "conduct-loop-owned",
            "Acceptance gate is owned by the conduct loop; reconciliation will handle terminal artifact"];
        yield return [verifying with { ParallelAcceptanceEnabled = false }, 2, "parallel-acceptance-disabled",
            "Acceptance gate running in background; reconciliation will handle terminal artifact"];
        yield return [verified with { GoalStatus = GoalStatus.Completed }, 3, "completed-goal",
            "Completed goal requires operator acceptance; background acceptance cannot reopen a landed goal."];
        yield return [verified with { ReplacementLeaseAvailable = false,
            ReplacementLeaseReason = "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown" },
            4, "replacement-evidence-mutation-lease",
            "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown"];
        yield return [verifying with { CandidateBuilt = false }, 5, "no-acceptance-candidate",
            "Acceptance gate running in background; reconciliation will handle terminal artifact"];
        yield return [verified with { AdmissionAdmitted = false,
            AdmissionReason = "acceptance width 1 reached; live acceptance occupants goal:abcd1234; retry on next conduct tick" },
            6, "acceptance-width-admission",
            "acceptance width 1 reached; live acceptance occupants goal:abcd1234; retry on next conduct tick"];
        yield return [verified with { ArtifactWriterBusy = true, ArtifactWriterMessage = "fixture writer lease busy" },
            7, "acceptance-artifact-writer-busy",
            "Acceptance artifact writer busy; retry on next conduct tick. fixture writer lease busy"];
        var noTick = verified with { IsConductorTick = false, AttemptDecisionKind = "Running", AttemptId = "attempt-123" };
        yield return [noTick with { NoTickWaitOutcome = "deadline-elapsed" }, 8, "no-tick-wait-deadline",
            "Acceptance verification remains in background after bounded no-tick wait; attempt=attempt-123."];
        yield return [noTick with { NoTickWaitOutcome = "reconciliation-ownership-changed" },
            8, "no-tick-reconciliation-ownership-changed",
            "Acceptance attempt reconciliation ownership changed; attempt=attempt-123."];
        yield return [noTick with { NoTickWaitOutcome = "ownership-changed" }, 8, "no-tick-attempt-ownership-changed",
            "Acceptance attempt metadata or ownership changed; attempt=attempt-123."];
        foreach (var kind in new[] { "Started", "Running" })
            yield return [verified with { AttemptDecisionKind = kind, AttemptId = "attempt-123" },
                9, "attempt-running-in-background",
                "Acceptance verification running in background; attempt=attempt-123."];
    }

    [Xunit.Theory]
    [Xunit.InlineData("Completed", "attempt-completed", "Acceptance attempt completed.")]
    [Xunit.InlineData("TerminalWithoutRun", "attempt-terminal-without-run", "Acceptance attempt terminal without run.")]
    public void TerminalAttempt_Proceeds(string kind, string evidence, string reason)
    {
        var decision = VerifyingStagePolicy.Evaluate(new(GoalLifecycleState.Verified, true, GoalStatus.Verified,
            AttemptDecisionKind: kind, AttemptId: "attempt-123"));

        Assert.Equal(VerifyingStageAction.Proceed, decision.Action);
        Assert.Equal(0, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void VerifiedCaller_NullHandBack_Proceeds(bool disabled)
    {
        var decision = VerifyingStagePolicy.Evaluate(new(GoalLifecycleState.Verified, true, GoalStatus.Verified,
            ParallelAcceptanceEnabled: !disabled, CandidateBuilt: disabled ? null : false));

        Assert.Equal(VerifyingStageAction.Proceed, decision.Action);
        Assert.Equal(0, decision.DiscriminatingRung);
        Assert.Equal("fallback-not-applicable", decision.DiscriminatingEvidence);
        Assert.Equal("Fallback acceptance not applicable.", decision.Reason);
    }

    [Xunit.Fact]
    public void EarlierHold_PrecedesLaterFacts()
    {
        var decision = VerifyingStagePolicy.Evaluate(new(GoalLifecycleState.Verifying, false, GoalStatus.Completed,
            ParallelAcceptanceEnabled: false, ReplacementLeaseAvailable: false, AdmissionAdmitted: false));

        Assert.Equal(VerifyingStageAction.Hold, decision.Action);
        Assert.Equal(1, decision.DiscriminatingRung);
        Assert.Equal("conduct-loop-owned", decision.DiscriminatingEvidence);
    }

    [Xunit.Fact]
    public void RecordedFacts_MissingDuplicateOrUnknownName_FailsLoudly()
    {
        var facts = new VerifyingStageFacts(GoalLifecycleState.Verifying, false, GoalStatus.Verifying).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => VerifyingStageFacts.FromRecordedFacts(facts.Skip(1).ToArray()));
        Assert.Throws<InvalidOperationException>(() => VerifyingStageFacts.FromRecordedFacts(
            facts.Select(f => f.Name == "conductorTick" ? f with { Name = "callerState" } : f).ToArray()));
        Assert.Throws<InvalidOperationException>(() => VerifyingStageFacts.FromRecordedFacts(
            facts.Select(f => f.Name == "candidate" ? f with { Name = "unknown" } : f).ToArray()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("callerState", "unknown")]
    [Xunit.InlineData("callerState", "1")]
    [Xunit.InlineData("goalStatus", "completed")]
    [Xunit.InlineData("goalStatus", "999")]
    [Xunit.InlineData("conductorTick", "")]
    [Xunit.InlineData("conductorTick", "True")]
    [Xunit.InlineData("parallelAcceptance", "unknown")]
    [Xunit.InlineData("attemptDecision", "unknown")]
    [Xunit.InlineData("noTickWait", "unknown")]
    public void RecordedFacts_MalformedValue_FailsLoudly(string name, string value)
    {
        var facts = new VerifyingStageFacts(GoalLifecycleState.Verifying, false, GoalStatus.Verifying).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => VerifyingStageFacts.FromRecordedFacts(
            facts.Select(f => f.Name == name ? f with { Value = value } : f).ToArray()));
    }

    [Xunit.Fact]
    public void IncompleteHoldEvidence_FailsLoudly()
    {
        var facts = new VerifyingStageFacts(GoalLifecycleState.Verified, true, GoalStatus.Verified);
        Assert.Throws<InvalidOperationException>(() => VerifyingStagePolicy.Evaluate(facts));
        Assert.Throws<InvalidOperationException>(() => VerifyingStagePolicy.Evaluate(facts with { ReplacementLeaseAvailable = false }));
        Assert.Throws<InvalidOperationException>(() => VerifyingStagePolicy.Evaluate(facts with { AdmissionAdmitted = false }));
        Assert.Throws<InvalidOperationException>(() => VerifyingStagePolicy.Evaluate(facts with { AttemptDecisionKind = "Running" }));
        Assert.Throws<InvalidOperationException>(() => VerifyingStagePolicy.Evaluate(facts with { NoTickWaitOutcome = "deadline-elapsed" }));
    }
}
