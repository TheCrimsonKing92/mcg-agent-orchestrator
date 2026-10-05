using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static ConductorDriverTestsLandingCompletionDecision;

// Parallel-safe: each test owns its goal and all effects are injected; no git or processes.
public sealed class ConductorDriverTestsLandingRebaseDecision
{
    [Fact]
    public void MissingPreLandingBranchCarriesRetirementDecisionThroughDonePayload()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var rebases = 0;
        var driver = MakeDriver(rebaseOntoMain: _ =>
        {
            rebases++;
            return new(GoalWorktreeRebaseStatus.MissingBranch, "goal/test", "branch is gone", [], null);
        }, runAcceptanceSummary: _ => throw new InvalidOperationException("Missing branch must not start acceptance."),
            land: _ => throw new InvalidOperationException("Missing branch must not land."),
            writeEscalation: (_, _, _) => throw new InvalidOperationException("Missing branch must retire instead of escalating."));

        var done = Assert.IsType<ConductorAdvanceOutcome.Done>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(1, rebases);
        Assert.Equal(GoalLifecycleState.CleanedUp, done.State);
        var decision = AssertDecisionAndPayload(done, "landing-rebase", "Retire", 1, "missing-branch-retired",
            "Conductor tick retired missing goal branch before landing because the goal artifact could not be rebased: branch is gone", "CleanedUp", null);
        AssertFact(decision, "phase", "pre-landing");
        AssertFact(decision, "rebaseStatus", "MissingBranch");
        AssertFact(decision, "updatedBranch", "false");
        AssertFact(decision, "applySideEffects", "true");
    }

    [Fact]
    public void PreMergeConflictCarriesRebaseDecisionAndOriginalEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var effects = new List<string>();
        var driver = MakeDriver(rebaseOntoMain: _ =>
        {
            effects.Add("rebase");
            return new(GoalWorktreeRebaseStatus.Conflict, "goal/test", "conflicts", ["src/A.cs", "src/B.cs"], null);
        }, land: _ => throw new InvalidOperationException("Conflicting pre-merge rebase must not land."),
            writeEscalation: (_, state, reason) =>
            {
                Assert.Equal(GoalLifecycleState.Verified, state);
                Assert.Equal("pre-merge rebase conflict (src/A.cs, src/B.cs); use 'workspace rebase' to resolve", reason);
                effects.Add("escalation");
            });
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/A.cs"], "branch", "main");

        var result = driver.CompleteParallelLandingAcceptance(candidate, ConductorAutonomyPolicy.Conservative,
            AcceptanceVerificationSummary.PassedWithNoUnmetCriteria, out var leaseHeld);
        Assert.False(leaseHeld);
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, escalated.State);
        Assert.Equal("pre-merge rebase conflict (src/A.cs, src/B.cs); use 'workspace rebase' to resolve", escalated.Reason);
        Assert.Null(escalated.Kind);
        Assert.Equal(new[] { "rebase", "escalation" }, effects);
        var decision = AssertDecisionAndPayload(escalated, "landing-rebase", "Escalate", 2, "rebase-conflict",
            escalated.Reason, "Verified", "Unspecified");
        AssertFact(decision, "phase", "pre-merge");
        AssertFact(decision, "conflictFiles", "src/A.cs\nsrc/B.cs");
        AssertFact(decision, "applySideEffects", "true");
    }
}
