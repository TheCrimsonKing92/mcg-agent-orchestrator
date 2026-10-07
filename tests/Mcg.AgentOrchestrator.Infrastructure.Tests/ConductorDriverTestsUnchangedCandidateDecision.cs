using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsUnchangedCandidateDecision
{
    [Xunit.Fact]
    public void SameCandidateHoldCarriesAttributedDecisionIntoTickPayload()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        var task = Assert.Single(goal.Tasks);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        ConductorDriverTests.DispatchTask(kernel, goal, task);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "",
                new DateTimeOffset(2026, 10, 7, 9, 15, 30, TimeSpan.FromHours(5.5)),
                WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, task.Id, "retry without new input", RetryCause.UnchangedContextRepeat);
        var dispatches = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => identity);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(0, dispatches);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var typed = Assert.IsType<UnchangedCandidateHoldReason>(held.TypedReason);
        Assert.Equal(task.RequiredRole, typed.Role);
        Assert.Equal(typed.Render(), held.Reason);
        Assert.Equal(identity.Canonical, held.StableIdentity);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("unchanged-candidate", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("unchanged-candidate", decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Equal(task.RequiredRole.ToString(), Assert.Single(decision.Facts, fact => fact.Name == "role").Value);
        Assert.Equal(goal.Id.Value, Assert.Single(decision.Facts, fact => fact.Name == "goalId").Value);
        Assert.Equal(identity.Canonical, Assert.Single(decision.Facts, fact => fact.Name == "candidateIdentity").Value);
        Assert.Equal(decision, VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(result.Outcome).Decision);
    }
}
