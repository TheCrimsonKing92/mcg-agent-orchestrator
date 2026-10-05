using Mcg.AgentOrchestrator.Core;

public sealed class VerifiedAdmissionPolicyTests
{
    [Theory]
    [InlineData(0, VerifiedAdmissionAction.Proceed, "verified-admission-proceed", "", "Verified admission checks passed.")]
    [InlineData(1, VerifiedAdmissionAction.Hold, "owner-review-hold", "owner-review-hold:owner-sha:fingerprint", "Owner review required.")]
    [InlineData(2, VerifiedAdmissionAction.Escalate, "cohort-attribution-failure", "", "Acceptance verification failed; review and fix before landing. cohort-attribution=cohort-1")]
    [InlineData(3, VerifiedAdmissionAction.Hold, "acceptance-apparatus-hold", "acceptance-apparatus:branch-sha:main-sha", "Acceptance apparatus hold remains active for unchanged candidate branch=branch-sha main=main-sha. Repair main or confirm acceptance-retry before another acceptance process starts.")]
    [InlineData(4, VerifiedAdmissionAction.Hold, "evidence-mutation-lease-hold", "", "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown")]
    [InlineData(5, VerifiedAdmissionAction.Hold, "task-verification-precheck", "", "Goal is not ready for acceptance: complete every task with a passed verification before accepting this gate.")]
    [InlineData(6, VerifiedAdmissionAction.Hold, "gate-start-infrastructure-deferred", "", "Acceptance infrastructure deferred (apparatus); retry on next conduct tick. unavailable")]
    [InlineData(7, VerifiedAdmissionAction.Hold, "gate-start-build-slots-busy", "", "Stable dotnet build slots busy; retry on next conduct tick. wanted-by=w; busy slots: slot-0 pid 7")]
    [InlineData(8, VerifiedAdmissionAction.Hold, "gate-start-build-lock-blocked", "", "Build artifact lock blocked acceptance; retry on next conduct tick. lock attribution")]
    [InlineData(9, VerifiedAdmissionAction.Hold, "gate-start-attempt-cancelled", "", "Acceptance attempt stopped by cancellation probe (StoppedDisposition); retry when the goal is eligible.")]
    public void EachRungPreservesOriginalOutcomeText(int rung, VerifiedAdmissionAction action,
        string evidence, string identity, string reason)
    {
        var facts = FactsForRung(rung);
        var decision = VerifiedAdmissionPolicy.Evaluate(facts);
        AssertDecision(decision);
        var recorded = facts.ToRecordedFacts();
        Assert.Equal(20, recorded.Count);
        Assert.Equal(20, recorded.Select(fact => fact.Name).Distinct(StringComparer.Ordinal).Count());
        var restored = VerifiedAdmissionFacts.FromRecordedFacts(recorded).ToRecordedFacts();
        for (var index = 0; index < recorded.Count; index++)
        {
            Assert.Equal(recorded[index].Name, restored[index].Name);
            Assert.Equal(recorded[index].Value, restored[index].Value);
        }
        AssertDecision(VerifiedAdmissionPolicy.Evaluate(VerifiedAdmissionFacts.FromRecordedFacts(recorded)));

        void AssertDecision(VerifiedAdmissionDecision actual)
        {
            Assert.Equal(action, actual.Action);
            Assert.Equal(rung, actual.DiscriminatingRung);
            Assert.Equal(evidence, actual.DiscriminatingEvidence);
            Assert.Equal(identity, actual.StableIdentity);
            Assert.Equal(reason, actual.Reason);
            Assert.Equal("verified-admission", actual.ToRecord().Stage);
        }
    }

    internal static VerifiedAdmissionFacts FactsForRung(int rung)
    {
        var facts = new VerifiedAdmissionFacts(GoalStatus.Verified)
        {
            OwnerReviewHold = false, CohortAttribution = false, ApparatusHold = false,
            EvidenceLease = true, AllTasksPassed = true
        };
        return rung switch
        {
            0 => facts,
            1 => facts with { OwnerReviewHold = true, OwnerReviewSha = "owner-sha",
                OwnerReviewReason = "Owner review required.", OwnerReviewFingerprint = "fingerprint" },
            2 => facts with { CohortAttribution = true, CohortAttributionEvidence = "cohort-attribution=cohort-1" },
            3 => facts with { ApparatusHold = true, ApparatusBranchSha = "branch-sha",
                ApparatusMainSha = "main-sha", ApparatusCandidate = "branch=branch-sha main=main-sha" },
            4 => facts with { EvidenceLease = false, EvidenceLeaseReason = "acceptance lease acquisition blocked; owner=unknown; expiresAtUtc=unknown" },
            5 => facts with { AllTasksPassed = false },
            6 => facts with { GateStartDeferral = "infrastructure-deferred",
                InfrastructureDeferredReasonCode = "apparatus", InfrastructureDeferredMessage = "unavailable" },
            7 => facts with { GateStartDeferral = "build-slots-busy", BuildSlotsBusyDetail = "wanted-by=w; busy slots: slot-0 pid 7" },
            8 => facts with { GateStartDeferral = "build-lock-blocked", BuildLockBlockedDetail = "lock attribution" },
            9 => facts with { GateStartDeferral = "attempt-cancelled", CancellationProbeCause = "StoppedDisposition" },
            _ => throw new InvalidOperationException("Unsupported test rung.")
        };
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EarlierRungsWinOverLaterObservedFailures(int rung)
    {
        var facts = FactsForRung(1) with
        {
            OwnerReviewHold = rung == 1,
            CohortAttribution = rung <= 2, CohortAttributionEvidence = "cohort-attribution=cohort-1",
            ApparatusHold = rung <= 3, ApparatusBranchSha = "branch-sha",
            ApparatusMainSha = "main-sha", ApparatusCandidate = "branch=branch-sha main=main-sha",
            EvidenceLease = rung > 4, EvidenceLeaseReason = "lease held",
            AllTasksPassed = false, GateStartDeferral = "attempt-cancelled", CancellationProbeCause = "StoppedDisposition"
        };
        Assert.Equal(rung, VerifiedAdmissionPolicy.Evaluate(facts).DiscriminatingRung);
    }

    [Fact]
    public void EmptyIdentityComponentsAndDetailRemainByteIdenticalOnReplay()
    {
        var owner = FactsForRung(1) with { OwnerReviewSha = "", OwnerReviewFingerprint = "" };
        var apparatus = FactsForRung(3) with { ApparatusBranchSha = "", ApparatusMainSha = "" };
        var deferral = FactsForRung(6) with { InfrastructureDeferredReasonCode = "", InfrastructureDeferredMessage = "" };
        foreach (var facts in new[] { owner, apparatus, deferral })
        {
            var original = VerifiedAdmissionPolicy.Evaluate(facts);
            var replay = VerifiedAdmissionPolicy.Evaluate(VerifiedAdmissionFacts.FromRecordedFacts(facts.ToRecordedFacts()));
            Assert.Equal(original.StableIdentity, replay.StableIdentity);
            Assert.Equal(original.Reason, replay.Reason);
        }
        Assert.Equal("owner-review-hold::", VerifiedAdmissionPolicy.Evaluate(owner).StableIdentity);
        Assert.Equal("acceptance-apparatus::", VerifiedAdmissionPolicy.Evaluate(apparatus).StableIdentity);
    }

    [Fact]
    public void UnidentifiedOrMissingRequiredInputsFailLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => VerifiedAdmissionPolicy.Evaluate(new(GoalStatus.Verified)));
        Assert.Throws<InvalidOperationException>(() => VerifiedAdmissionPolicy.Evaluate(FactsForRung(1) with { OwnerReviewReason = null }));
        Assert.Throws<InvalidOperationException>(() => VerifiedAdmissionPolicy.Evaluate(FactsForRung(0) with { GateStartDeferral = "unknown" }));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("boolean")]
    [InlineData("status")]
    [InlineData("deferral")]
    public void MalformedRecordedFactsFailLoudly(string corruption)
    {
        var facts = FactsForRung(0).ToRecordedFacts().ToList();
        switch (corruption)
        {
            case "count": facts.RemoveAt(0); break;
            case "duplicate": facts[1] = facts[0]; break;
            case "missing": facts[1] = new("unknown", "false"); break;
            case "boolean": facts[1] = new("ownerReviewHold", "yes"); break;
            case "status": facts[0] = new("goalStatus", "999"); break;
            case "deferral": facts[14] = new("gateStartDeferral", "unknown"); break;
        }
        Assert.Throws<InvalidOperationException>(() => VerifiedAdmissionFacts.FromRecordedFacts(facts));
    }
}
