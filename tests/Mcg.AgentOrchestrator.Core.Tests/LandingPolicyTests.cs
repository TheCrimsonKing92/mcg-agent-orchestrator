using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: pure policy inputs, no shared state or effects.
public sealed class LandingPolicyTests
{
    [Theory]
    [InlineData(1, "diff-scope-unknown", "diff scope unknown: missing goal branch")]
    [InlineData(2, "acceptance-not-accepted", "acceptance verification not passed")]
    [InlineData(3, "criterion-evidence-rebind-outstanding", "criterion 2 requires fresh evidence")]
    [InlineData(4, "ownership-hold", "ownership-denylist hold: 2 task(s) touched RequiresOperatorApproval path(s)")]
    [InlineData(5, "ownership-hold-unattributable", "ownership-denylist diff touched RequiresOperatorApproval path(s), but no writing task attribution was available")]
    [InlineData(6, "integration-not-on-bound-main", "integration branch contains state not present on bound main")]
    [InlineData(0, "landing-proceed", "Landing pre-mutation checks passed.")]
    public void ObservedRung_PreservesActionEvidenceAndExecutorReason(int rung, string evidence, string reason)
    {
        var decision = LandingPolicy.Evaluate(FactsForRung(rung));
        Assert.Equal(rung == 0 ? LandingPolicyAction.Proceed : LandingPolicyAction.Escalate, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        var record = decision.ToRecord();
        Assert.Equal("landing", record.Stage);
        Assert.Equal(decision.Action.ToString(), record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(10, record.Facts.Count);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "  candidate verification is stale  ")]
    [InlineData(true, "  candidate verification is stale  ")]
    [InlineData(false, "   ")]
    [InlineData(true, "   ")]
    public void UnacceptedCandidate_UsesEngineReasonForLiveRiskAndPolicy(bool permissive, string? hold)
    {
        var facts = FactsForRung(2) with { AcceptanceHoldDescription = hold };
        var engine = Assert.IsType<LandingDecision.Escalate>(LandingDecisionEngine.Decide(new LandingInputs(
            RepositoryChangeClassifier.Classify(["src/security/Auth.cs", "Directory.Build.props"]),
            AcceptancePassed: false, IntegrationToMainIsCleanFastForward: true, GoalFailureRetryCount: 0,
            Policy: permissive ? ConductorAutonomyPolicy.Permissive : ConductorAutonomyPolicy.Conservative,
            AcceptanceHoldDescription: hold)));
        var decision = LandingPolicy.Evaluate(facts);
        Assert.Equal(LandingPolicyAction.Escalate, decision.Action);
        Assert.Equal(2, decision.DiscriminatingRung);
        Assert.Equal("acceptance-not-accepted", decision.DiscriminatingEvidence);
        Assert.Equal(engine.Reason, decision.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MultipleBlockers_FirstObservedRungWins(int rung)
    {
        var facts = new LandingFacts
        {
            ChangedFilesResolved = rung > 1, AcceptanceAccepted = rung > 2,
            EvidenceRebindOutstanding = rung <= 3, EvidenceDiagnostic = "diagnostic",
            OwnershipRequiresApproval = rung <= 5, AllowsAutonomousHighRiskOwnership = false,
            AttributableHoldRequestCount = rung == 5 ? 0 : 2, IntegrationAncestry = "diverged"
        };
        Assert.Equal(rung, LandingPolicy.Evaluate(facts).DiscriminatingRung);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnershipApproval_AutonomousAllowanceControlsHold(bool? allows)
    {
        var facts = FactsForRung(4) with { AllowsAutonomousHighRiskOwnership = allows, IntegrationAncestry = "on-bound-main" };
        Assert.Equal(allows == true ? 0 : 4, LandingPolicy.Evaluate(facts).DiscriminatingRung);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("on-bound-main")]
    public void AcceptedAncestry_Proceeds(string ancestry)
    {
        Assert.Equal(LandingPolicyAction.Proceed, LandingPolicy.Evaluate(FactsForRung(0) with { IntegrationAncestry = ancestry }).Action);
    }

    [Fact]
    public void EmptyFailureAndDiagnostic_PreserveExecutorText()
    {
        Assert.Equal("diff scope unknown: ", LandingPolicy.Evaluate(FactsForRung(1) with { ChangedFilesFailureReason = null }).Reason);
        var diagnostic = LandingPolicy.Evaluate(FactsForRung(3) with { EvidenceDiagnostic = "" });
        Assert.Equal(3, diagnostic.DiscriminatingRung);
        Assert.Equal("", diagnostic.Reason);
    }

    [Theory]
    [InlineData("changedFilesResolved", "True")]
    [InlineData("acceptanceAccepted", "unknown")]
    [InlineData("attributableHoldRequestCount", "-1")]
    [InlineData("attributableHoldRequestCount", "2147483648")]
    [InlineData("integrationAncestry", "clean")]
    public void MalformedRecordedFact_FailsLoudly(string name, string value)
    {
        var facts = FactsForRung(0).ToRecordedFacts().Select(f => f.Name == name ? new PolicyDecisionFact(name, value) : f).ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingFacts.FromRecordedFacts(facts));
    }

    [Fact]
    public void MissingAndDuplicateFacts_FailLoudly()
    {
        var facts = FactsForRung(0).ToRecordedFacts().ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingFacts.FromRecordedFacts(facts[..9]));
        facts[9] = facts[0];
        Assert.Throws<InvalidOperationException>(() => LandingFacts.FromRecordedFacts(facts));
        facts[9] = new("unrecognized", "absent");
        Assert.Throws<InvalidOperationException>(() => LandingFacts.FromRecordedFacts(facts));
    }

    [Fact]
    public void IncompleteOrUnsafeObservations_FailLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(new LandingFacts()));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(0) with { AcceptanceAccepted = null }));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(0) with { IntegrationAncestry = null }));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(4) with { AttributableHoldRequestCount = null }));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(4) with { AttributableHoldRequestCount = -1 }));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(3) with { EvidenceDiagnostic = null }));
        Assert.Throws<InvalidOperationException>(() => LandingPolicy.Evaluate(FactsForRung(0) with { IntegrationAncestry = "clean" }));
    }

    internal static LandingFacts FactsForRung(int rung)
    {
        var facts = new LandingFacts { ChangedFilesResolved = true };
        if (rung == 1)
            return facts with { ChangedFilesResolved = false, ChangedFilesFailureReason = "missing goal branch" };
        facts = facts with { AcceptanceAccepted = true };
        if (rung == 2)
            return facts with { AcceptanceAccepted = false };
        facts = facts with { EvidenceRebindOutstanding = false };
        if (rung == 3)
            return facts with { EvidenceRebindOutstanding = true, EvidenceDiagnostic = "criterion 2 requires fresh evidence" };
        facts = facts with { OwnershipRequiresApproval = false, AllowsAutonomousHighRiskOwnership = false };
        if (rung is 4 or 5)
            return facts with { OwnershipRequiresApproval = true, AttributableHoldRequestCount = rung == 4 ? 2 : 0 };
        return facts with { IntegrationAncestry = rung == 6 ? "diverged" : "absent" };
    }
}
