using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each test owns its goal and all effects are injected; no git or processes.
public sealed class ConductorDriverTestsLandingCompletionDecision
{
    [Fact]
    public void FailedVerificationCarriesCompletionDecisionAndOriginalEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            effects.Add("acceptance");
            return new AcceptanceVerificationSummary(false, [], FailureDetail: "  check failed  ");
        }, writeEscalation: (_, state, reason) =>
        {
            Assert.Equal(GoalLifecycleState.Verified, state);
            Assert.Equal("Acceptance verification failed; review and fix before landing. Acceptance output tail: check failed", reason);
            effects.Add("escalation");
        }, land: _ => throw new InvalidOperationException("Failed acceptance must not land."));

        var outcome = Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(GoalLifecycleState.Verified, outcome.State);
        Assert.Equal("Acceptance verification failed; review and fix before landing. Acceptance output tail: check failed", outcome.Reason);
        Assert.Equal(ConductorEscalationKind.AcceptanceVerificationFailed, outcome.Kind);
        Assert.Equal(new[] { "acceptance", "escalation" }, effects);
        var decision = AssertDecisionAndPayload(outcome, "landing-completion", "Escalate", 3, "acceptance-verification-failed",
            outcome.Reason, "Verified", "AcceptanceVerificationFailed");
        AssertFact(decision, "runPassed", "false");
        AssertFact(decision, "unmetCriteriaCount", "0");
        AssertFact(decision, "timedOut", "false");
        AssertFact(decision, "failureTail", " Acceptance output tail: check failed");
        Assert.Null(LandingCompletionPolicy.Evaluate(LandingCompletionFacts.FromRecordedFacts(decision.Facts)).StableIdentity);
    }

    [Fact]
    public void MutationBoundaryCarriesCompletionDecisionWithoutLandingOrEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            effects.Add("acceptance");
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        }, clearCriterionRetryFeedback: (_, _) => effects.Add("clear-feedback"),
            land: _ => throw new InvalidOperationException("Mutation hold must not land."),
            writeEscalation: (_, _, _) => throw new InvalidOperationException("Mutation hold must not record escalation."));
        driver.LandingMutationBlocker = () =>
        {
            effects.Add("mutation-check");
            return "acceptance engine circuit is Pending";
        };

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal("Landing held at mutation boundary: acceptance engine circuit is Pending", held.Reason);
        Assert.Null(held.StableIdentity);
        Assert.Null(held.TypedReason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Equal(new[] { "acceptance", "clear-feedback", "mutation-check" }, effects);
        var decision = AssertDecisionAndPayload(held, "landing-completion", "Hold", 8, "landing-mutation-boundary-hold",
            held.Reason, "Verified", null);
        AssertFact(decision, "mutationBlockReason", "acceptance engine circuit is Pending");
    }

    [Theory]
    [InlineData("ownership-denylist hold: task touched protected path", 10, "landing-ownership-hold", false, false)]
    [InlineData("landing mutation blocked: circuit opened inside landing", 9, "landing-mutation-hold-escalation", true, false)]
    [InlineData("Landing held at mutation boundary: circuit opened inside landing", 11, "landing-escalation", false, true)]
    [InlineData("integration->main conflict", 11, "landing-escalation", false, true)]
    public void LandingResultCarriesDecisionAndPreservesEscalationEffects(string reason, int rung, string evidence, bool isHold, bool recordsEscalation)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            effects.Add("acceptance");
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        }, land: g =>
        {
            effects.Add("land");
            return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Escalate(reason), "integration", false, "Held");
        }, writeEscalation: (_, state, recordedReason) =>
        {
            Assert.Equal(GoalLifecycleState.Verified, state);
            Assert.Equal(reason, recordedReason);
            effects.Add("escalation");
        }, runAdvisorySemanticAcceptance: (_, _) => throw new InvalidOperationException("Escalated landing must not run semantic acceptance."),
            afterSuccessfulLanding: (_, _) => throw new InvalidOperationException("Escalated landing must not run successful-landing effects."));

        var outcome = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome;
        Assert.Equal(GoalStatus.Verified, goal.Status);
        if (isHold)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Equal(reason, held.Reason);
            Assert.Null(held.StableIdentity);
            Assert.Null(held.TypedReason);
            Assert.Equal(ConductorHoldOwner.None, held.Owner);
        }
        else
        {
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(outcome);
            Assert.Equal(GoalLifecycleState.Verified, escalated.State);
            Assert.Equal(reason, escalated.Reason);
            Assert.Null(escalated.Kind);
        }
        Assert.Equal(recordsEscalation ? new[] { "acceptance", "land", "escalation" } : new[] { "acceptance", "land" }, effects);
        var decision = AssertDecisionAndPayload(outcome, "landing-completion", isHold ? "Hold" : "Escalate", rung, evidence,
            reason, "Verified", isHold ? null : "Unspecified");
        AssertFact(decision, "landingReason", reason);
        AssertFact(decision, "landingResultKind", rung == 9 ? "mutation-hold" : rung == 10 ? "ownership-hold" : "escalate");
        var replay = LandingCompletionPolicy.Evaluate(LandingCompletionFacts.FromRecordedFacts(decision.Facts));
        Assert.Null(replay.StableIdentity);
        Assert.Null(replay.EscalationKind);
    }

    [Fact]
    public void PromotedLandingKeepsExecutedPayloadWithoutDecision()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(land: g =>
        {
            effects.Add("land");
            return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
        }, runAdvisorySemanticAcceptance: (_, _) => effects.Add("semantic"), afterSuccessfulLanding: (_, _) => effects.Add("after-landing"));
        driver.SuccessfulLandingSink = _ => effects.Add("receipt");
        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(GoalLifecycleState.Verified, executed.FromState);
        Assert.Equal("Landed: Landed", executed.Description);
        Assert.Equal(new[] { "land", "receipt", "semantic", "after-landing" }, effects);
        Assert.Null(VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(executed).Decision);
    }

    internal static PolicyDecisionRecord AssertDecisionAndPayload(ConductorAdvanceOutcome outcome, string stage, string action,
        int rung, string evidence, string reason, string state, string? kind)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(outcome switch
        {
            ConductorAdvanceOutcome.Held held => held.Decision,
            ConductorAdvanceOutcome.Escalated escalated => escalated.Decision,
            ConductorAdvanceOutcome.Done done => done.Decision,
            _ => throw new InvalidOperationException("Expected a held, escalated or done outcome.")
        });
        Assert.Equal(stage, decision.Stage);
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        Assert.Equal(outcome is ConductorAdvanceOutcome.Held ? "Held" : outcome is ConductorAdvanceOutcome.Done ? "Done" : "Escalated", payload.OutcomeKind);
        Assert.Equal(state, payload.LifecycleState);
        Assert.Equal(kind, payload.EscalationKind);
        var copied = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(decision.Stage, copied.Stage);
        Assert.Equal(decision.Action, copied.Action);
        Assert.Equal(decision.Rung, copied.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, copied.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, copied.Reason);
        Assert.Equal(stage == "landing-completion" ? 18 : 6, decision.Facts.Count);
        Assert.Equal(decision.Facts.Count, copied.Facts.Count);
        for (var index = 0; index < decision.Facts.Count; index++)
        {
            Assert.Equal(decision.Facts[index].Name, copied.Facts[index].Name);
            Assert.Equal(decision.Facts[index].Value, copied.Facts[index].Value);
        }
        return decision;
    }

    internal static void AssertFact(PolicyDecisionRecord decision, string name, string value) =>
        Assert.Equal(value, Assert.Single(decision.Facts, fact => fact.Name == name).Value);
}
