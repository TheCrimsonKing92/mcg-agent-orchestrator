using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class LandingCompletionPolicyTests
{
    [Theory]
    [InlineData(1, LandingCompletionAction.Hold, "environmental-apparatus-hold", "Acceptance gate apparatus/environmental failure recorded for unchanged candidate branch=branch main=main. Acceptance will not re-run until the candidate or main HEAD changes, or an operator confirms acceptance-retry; no worker was reopened.", null, "acceptance-apparatus:branch:main")]
    [InlineData(2, LandingCompletionAction.Escalate, "acceptance-verification-timed-out", "Acceptance verification timed out; rerun acceptance after clearing the blocker. Acceptance output tail: check failed", null, null)]
    [InlineData(3, LandingCompletionAction.Escalate, "acceptance-verification-failed", "Acceptance verification failed; review and fix before landing. Acceptance output tail: check failed", ConductorEscalationKind.AcceptanceVerificationFailed, null)]
    [InlineData(4, LandingCompletionAction.Hold, "criterion-evidence-hold", "Criterion evidence is missing for candidate branch.", null, "criterion-evidence:branch")]
    [InlineData(5, LandingCompletionAction.Escalate, "unmet-criteria-no-retry-task", "Acceptance criteria unmet but no completed task is available to retry: criterion A; review/land manually", null, null)]
    [InlineData(6, LandingCompletionAction.Proceed, "unmet-criteria-retry", "Acceptance criteria retry is within budget.", null, null)]
    [InlineData(7, LandingCompletionAction.Escalate, "unmet-criteria-retries-exhausted", "Acceptance criteria unmet after 2 retries: criterion A; review/land manually", null, null)]
    [InlineData(8, LandingCompletionAction.Hold, "landing-mutation-boundary-hold", "Landing held at mutation boundary: acceptance engine circuit is Pending", null, null)]
    [InlineData(9, LandingCompletionAction.Hold, "landing-mutation-hold-escalation", "Landing held at mutation boundary: circuit opened inside landing", null, null)]
    [InlineData(10, LandingCompletionAction.Escalate, "landing-ownership-hold", "ownership-denylist hold: task touched protected path", null, null)]
    [InlineData(11, LandingCompletionAction.Escalate, "landing-escalation", "integration->main conflict", null, null)]
    [InlineData(0, LandingCompletionAction.Proceed, "landing-promote", "Landing promoted.", null, null)]
    public void EachRungPreservesItsDispositionAndExactReason(int rung, LandingCompletionAction action,
        string evidence, string reason, ConductorEscalationKind? kind, string? identity)
    {
        var decision = LandingCompletionPolicy.Evaluate(FactsForRung(rung));
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(kind, decision.EscalationKind);
        Assert.Equal(identity, decision.StableIdentity);
        var record = decision.ToRecord();
        Assert.Equal("landing-completion", record.Stage);
        Assert.Equal(action.ToString(), record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(18, record.Facts.Count);
    }

    public static LandingCompletionFacts FactsForRung(int rung)
    {
        var facts = new LandingCompletionFacts { ApparatusRun = false, RunPassed = true, UnmetCriteriaCount = 0 };
        return rung switch
        {
            1 => facts with { ApparatusRun = true, RunPassed = false, BranchSha = "branch", MainSha = "main", ApparatusCandidate = "branch=branch main=main" },
            2 or 3 => facts with { RunPassed = false, TimedOut = rung == 2, FailureTail = " Acceptance output tail: check failed" },
            4 => facts with { EvidenceHoldReason = "Criterion evidence is missing for candidate branch.", EvidenceHoldState = GoalLifecycleState.Verified, EvidenceHoldIdentity = "criterion-evidence:branch" },
            5 or 6 or 7 => facts with { RunPassed = false, UnmetCriteriaCount = 1, UnmetCriteria = "criterion A", RetryTaskAvailable = rung != 5, RetryCount = rung == 7 ? 2 : 0, RetryBudget = 2 },
            8 => facts with { MutationBlockReason = "acceptance engine circuit is Pending" },
            9 => facts with { LandingResultKind = "mutation-hold", LandingReason = "Landing held at mutation boundary: circuit opened inside landing" },
            10 => facts with { LandingResultKind = "ownership-hold", LandingReason = "ownership-denylist hold: task touched protected path" },
            11 => facts with { LandingResultKind = "escalate", LandingReason = "integration->main conflict" },
            0 => facts with { LandingResultKind = "promote" },
            _ => throw new ArgumentOutOfRangeException(nameof(rung))
        };
    }

    [Fact]
    public void EarlierObservationsWinOverLaterLandingResults()
    {
        var facts = FactsForRung(1) with { MutationBlockReason = "blocked", LandingResultKind = "escalate", LandingReason = "conflict" };
        Assert.Equal(1, LandingCompletionPolicy.Evaluate(facts).DiscriminatingRung);
        Assert.Equal(4, LandingCompletionPolicy.Evaluate(FactsForRung(4) with
        { UnmetCriteriaCount = 1, RetryTaskAvailable = false, UnmetCriteria = "A" }).DiscriminatingRung);
        Assert.Equal(8, LandingCompletionPolicy.Evaluate(FactsForRung(10) with { MutationBlockReason = "blocked" }).DiscriminatingRung);
    }

    [Fact]
    public void NullApparatusHeadsPreserveUnknownIdentityOnReplay()
    {
        var original = LandingCompletionPolicy.Evaluate(FactsForRung(1) with { BranchSha = null, MainSha = null });
        var replay = LandingCompletionPolicy.Evaluate(LandingCompletionFacts.FromRecordedFacts(original.ToRecord().Facts));
        Assert.Equal("acceptance-apparatus:unknown:unknown", original.StableIdentity);
        Assert.Equal(original.StableIdentity, replay.StableIdentity);
    }

    [Fact]
    public void UnobservedOutcomeFailsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => LandingCompletionPolicy.Evaluate(new LandingCompletionFacts()));
        Assert.Throws<InvalidOperationException>(() => LandingCompletionPolicy.Evaluate(FactsForRung(3) with { TimedOut = null }));
        Assert.Throws<InvalidOperationException>(() => LandingCompletionPolicy.Evaluate(FactsForRung(7) with { RetryBudget = null }));
    }

    [Theory]
    [InlineData("runPassed", "TRUE")]
    [InlineData("unmetCriteriaCount", "-1")]
    [InlineData("retryCount", "many")]
    [InlineData("evidenceHoldState", "999")]
    [InlineData("landingResultKind", "unexpected")]
    public void MalformedRecordedValueFailsLoudly(string name, string value)
    {
        var recorded = FactsForRung(10).ToRecordedFacts().Select(f => f.Name == name ? new PolicyDecisionFact(name, value) : f).ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingCompletionFacts.FromRecordedFacts(recorded));
    }

    [Fact]
    public void IncompleteDuplicateAndMisnamedFactsCannotReplay()
    {
        var recorded = FactsForRung(10).ToRecordedFacts().ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingCompletionFacts.FromRecordedFacts(recorded[..^1]));
        recorded[^1] = recorded[0];
        Assert.Throws<InvalidOperationException>(() => LandingCompletionFacts.FromRecordedFacts(recorded));
        recorded[^1] = new("unknown", "");
        Assert.Throws<InvalidOperationException>(() => LandingCompletionFacts.FromRecordedFacts(recorded));
    }
}
