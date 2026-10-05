using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: owns the goal; all conductor effects are injected.
public sealed class ConductorDriverTestsLandingPolicyDecision
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void LandingEscalation_PreservesOutcomeAndUsesAttachedOrFallbackDecision(bool ownership, bool attach)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var facts = new LandingFacts
        {
            ChangedFilesResolved = true, AcceptanceAccepted = true, EvidenceRebindOutstanding = false,
            OwnershipRequiresApproval = ownership, AllowsAutonomousHighRiskOwnership = false,
            AttributableHoldRequestCount = ownership ? 1 : null,
            IntegrationAncestry = ownership ? null : "diverged"
        };
        var landingDecision = LandingPolicy.Evaluate(facts).ToRecord();
        var reason = ownership
            ? "ownership-denylist hold: 1 task(s) touched RequiresOperatorApproval path(s)"
            : "integration branch contains state not present on bound main";
        Assert.Equal(reason, landingDecision.Reason);
        var effects = new List<string>();
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            effects.Add("acceptance");
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        }, land: g =>
        {
            effects.Add("land");
            return new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Escalate(reason) { Decision = attach ? landingDecision : null }, "integration", false, "Held");
        }, writeEscalation: (_, state, recordedReason) =>
        {
            Assert.Equal(GoalLifecycleState.Verified, state);
            Assert.Equal(reason, recordedReason);
            effects.Add("escalation");
        }, runAdvisorySemanticAcceptance: (_, _) => throw new InvalidOperationException("Escalated landing must not run semantic acceptance."),
            afterSuccessfulLanding: (_, _) => throw new InvalidOperationException("Escalated landing must not run successful-landing effects."));

        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(GoalLifecycleState.Verified, outcome.State);
        Assert.Equal(reason, outcome.Reason);
        Assert.Null(outcome.Kind);
        Assert.Equal(ownership ? new[] { "acceptance", "land" } : new[] { "acceptance", "land", "escalation" }, effects);
        var recorded = Assert.IsType<PolicyDecisionRecord>(outcome.Decision);
        Assert.Equal(attach ? "landing" : "landing-completion", recorded.Stage);
        Assert.Equal("Escalate", recorded.Action);
        Assert.Equal(attach ? ownership ? 4 : 6 : ownership ? 10 : 11, recorded.Rung);
        Assert.Equal(attach
            ? ownership ? "ownership-hold" : "integration-not-on-bound-main"
            : ownership ? "landing-ownership-hold" : "landing-escalation", recorded.DiscriminatingEvidence);
        Assert.Equal(reason, recorded.Reason);
        Assert.Equal(attach ? 10 : 18, recorded.Facts.Count);
        if (attach)
            Assert.Same(landingDecision, recorded);
        else
            Assert.Equal(reason, Assert.Single(recorded.Facts, fact => fact.Name == "landingReason").Value);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        Assert.Equal("Escalated", payload.OutcomeKind);
        Assert.Equal("Verified", payload.LifecycleState);
        Assert.Equal("Unspecified", payload.EscalationKind);
        Assert.Same(recorded, payload.Decision);
    }
}
