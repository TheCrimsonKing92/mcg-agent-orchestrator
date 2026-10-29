using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each scenario owns its repository and databases; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PassedCohortReceiptApprovalHoldNegativeControlTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    [Trait("Category", "CrossTick")]
    public void MainAdvancedBeforeApproval_ReceiptNotReusedAndMembersAdmittedToOrdinaryGating()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        var held = scenario.Tick();
        Assert.Equal(2, held.Count(line => line.Contains("reason=PassedCohortReceiptApprovalHeld", StringComparison.Ordinal)));
        var beforeReceipt = JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value));

        File.WriteAllText(Path.Combine(scenario.Repo, "unrelated-main.txt"), "Advance main before owner approval.");
        RunGit(scenario.Repo, "add", "unrelated-main.txt");
        RunGit(scenario.Repo, "commit", "-m", "Advance main during cohort approval hold");
        var liveMain = RunGitOutput(scenario.Repo, "rev-parse", "main").Trim();
        Assert.NotEqual(scenario.Receipt.Identity.ObservedMainRevision, liveMain);
        Approve(scenario.Workspace, scenario.Goals[0], scenario.CandidateRevisions[0]);

        var candidates = Candidates(scenario);
        Assert.All(candidates, candidate => Assert.Equal(liveMain,
            Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult).Projection.MainRevision));
        Assert.Empty(scenario.Driver.FindLandablePassedCohortSelections(candidates));
        Assert.Null(Evaluate(scenario, candidates));

        // Hold the replacement gate at its verifier so no new receipt can land before inspection.
        scenario.BlockNextGate();
        var progress = scenario.Tick();
        scenario.AssertGateStarted();
        Assert.Equal(2, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Contains(goal.Id.Value, scenario.Driver.GetActiveCohortGateMemberGoalIds());
            Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id));
            AssertNoLanded(scenario.Workspace, goal.Id);
            Assert.NotEqual(GoalStatus.Completed, goal.Status);
        });
        Assert.DoesNotContain(progress, line => line.Contains("reason=PassedCohortReceiptApprovalHeld", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, line => line.Contains("prelanded=true", StringComparison.Ordinal));
        Assert.Equal(beforeReceipt, JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value)));
    }

    [Fact]
    public void NonAcceptanceCriterionObligation_IsNotReportedAndSelectionUnchanged()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        var candidates = Candidates(scenario);
        Assert.NotNull(Evaluate(scenario, candidates));
        var blocked = scenario.Goals[0];
        scenario.Kernel.MapCriterionEvidenceOwner(blocked.Id, 0, 1, CriterionEvidenceOwner.Operator,
            "negative control", expectedCandidateSha: scenario.CandidateRevisions[0]);
        Assert.Contains(blocked.OutstandingCriterionEvidenceObligations,
            obligation => obligation.Owner == CriterionEvidenceOwner.Operator);
        // Keep the same ready projections so the independent obligation itself disqualifies the hold.
        Assert.Null(Evaluate(scenario, candidates));

        var partner = scenario.Goals[1];
        Goal[] ordinaryEligible = [partner];
        var ordinaryCandidates = candidates.Where(candidate => candidate.GoalId == partner.Id).ToArray();
        Assert.Single(ordinaryCandidates);
        Assert.Null(ConductorAcceptanceCohortSelector.Select(ordinaryCandidates).Selection);
        var results = new Dictionary<string, ParallelLandingOutcome>();
        var progress = new List<string>();
        var beforeGoals = scenario.GoalState();
        var beforeReceipt = JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value));
        var (eligible, remaining) = PassedCohortReceiptApprovalHoldAdmission.Apply(
            scenario.Driver, ConductorAutonomyPolicy.Permissive, scenario.Kernel,
            ordinaryEligible, ordinaryCandidates, scenario.Goals, new HashSet<GoalId> { blocked.Id },
            _ => true, new HashSet<string>(), results, 1, progress.Add);

        Assert.Equal(ordinaryEligible.Select(goal => goal.Id), eligible.Select(goal => goal.Id));
        Assert.Equal(ordinaryCandidates, remaining);
        Assert.Null(ConductorAcceptanceCohortSelector.Select(remaining).Selection);
        Assert.Empty(results);
        Assert.Empty(progress);
        Assert.Equal(beforeGoals, scenario.GoalState());
        Assert.Equal(beforeReceipt, JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value)));
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal => Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id)));
    }

    private static ConductorSpeculativeAcceptanceCandidate[] Candidates(PassedCohortReceiptApprovalHoldTests.Scenario scenario) =>
        scenario.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            scenario.Driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, scenario.Kernel))).ToArray();

    private static PassedCohortReceiptApprovalHold.Decision? Evaluate(
        PassedCohortReceiptApprovalHoldTests.Scenario scenario, ConductorSpeculativeAcceptanceCandidate[] candidates)
    {
        Assert.All(candidates, candidate => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidate.ProjectionResult));
        var denylist = LandingExecutor.LoadLandingDenylist(scenario.Repo, scenario.Receipt.Identity.ObservedMainRevision).Denylist;
        var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
        return PassedCohortReceiptApprovalHold.Evaluate(scenario.Receipt,
            scenario.Goals.ToDictionary(goal => goal.Id), candidates.ToDictionary(candidate => candidate.GoalId,
                candidate => candidate.ProjectionResult), scenario.Goals.Select(goal => goal.Id).ToHashSet(),
            scenario.Driver.HasFinalizedCohortLanding, member => LandingDenylistOwnerGate.Evaluate(
                scenario.Repo, denylist, decisions, member.GoalId.Value,
                scenario.Receipt.Identity.ObservedMainRevision, member.CandidateRevision, member.LandingPaths));
    }
}
