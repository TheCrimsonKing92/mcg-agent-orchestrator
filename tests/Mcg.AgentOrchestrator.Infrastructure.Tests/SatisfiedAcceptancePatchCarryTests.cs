using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;
using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class SatisfiedAcceptancePatchCarryTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void PatchEquivalentSatisfiedAcceptanceRebindsOnPassingGate()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            var (kernel, goal) = CreateGoal(fixture.OldHead, true);
            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Null(diagnostic);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.NewHead, obligation.ExpectedCandidateSha);
            Assert.Equal(fixture.NewHead, obligation.CandidateSha);
            Assert.Equal($"full-acceptance:{fixture.NewHead}", obligation.ReceiptId);
            Assert.Equal(fixture.OldHead, Assert.Single(obligation.PriorReceipts!).CandidateSha);
            var note = Assert.Single(CarryNotes(fixture.Repository, goal));
            Assert.Contains(obligation.Id, note.Detail, StringComparison.Ordinal);
            Assert.Contains(fixture.OldHead, note.Detail, StringComparison.Ordinal);
            Assert.Contains(fixture.NewHead, note.Detail, StringComparison.Ordinal);
            Assert.Contains("patch-equivalent carry of satisfied acceptance obligation", note.Detail, StringComparison.Ordinal);

            Assert.Null(AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository));
            Assert.Equal(obligation, Assert.Single(goal.CriterionEvidenceObligations));
            Assert.Single(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void PatchEquivalentSatisfiedAcceptanceAllowsConductorToLand()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            var (kernel, goal) = CreateGoal(fixture.OldHead, true);
            PassVerification(kernel, goal, goal.Tasks.Single());
            var landCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [], BranchHeadSha: fixture.NewHead),
                resolveAcceptanceHeads: _ => (fixture.NewHead, fixture.MainHead),
                land: item =>
                {
                    landCalls++;
                    return new LandingResult(item.Id.Value, item.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                executionDirectory: fixture.Repository);

            driver.BeginTick(kernel, 1);
            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
            Assert.Equal(1, landCalls);
            Assert.Equal(fixture.NewHead, Assert.Single(goal.CriterionEvidenceObligations).CandidateSha);
            Assert.Single(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(PatchScenario.Edited)]
    [Xunit.InlineData(PatchScenario.Added)]
    [Xunit.InlineData(PatchScenario.Unresolvable)]
    public void DifferentOrUncomputablePatchRefusesWithoutRebinding(PatchScenario scenario)
    {
        var fixture = CreateFixture(scenario);
        try
        {
            var (kernel, goal) = CreateGoal(fixture.OldHead, true);
            var before = Assert.Single(goal.CriterionEvidenceObligations);

            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Equal(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{before.Id}' is bound to {fixture.OldHead}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: reason=not-eligible.",
                diagnostic);
            Assert.Equal(before, Assert.Single(goal.CriterionEvidenceObligations));
            Assert.Empty(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void FailedAcceptanceStillRefusesPatchEquivalentCandidate()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            var (kernel, goal) = CreateGoal(fixture.OldHead, false);
            var before = Assert.Single(goal.CriterionEvidenceObligations);

            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Equal(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{before.Id}' is bound to {fixture.OldHead}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: reason=not-eligible.",
                diagnostic);
            Assert.Equal(before, Assert.Single(goal.CriterionEvidenceObligations));
            Assert.Empty(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void FailedObligationPreventsAnySatisfiedRebind()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            var (kernel, goal) = CreateGoal(fixture.OldHead, false, true);
            var before = goal.CriterionEvidenceObligations.ToArray();

            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Equal(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{before[0].Id}' is bound to {fixture.OldHead}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: reason=not-eligible.",
                diagnostic);
            Assert.Equal(before, goal.CriterionEvidenceObligations.ToArray());
            Assert.Empty(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void NonEquivalentSatisfiedObligationPreventsEquivalentSiblingRebind()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            AppendCommit(fixture.Repository, "src/third.txt", "different patch");
            var differentHead = ReadGit(fixture.Repository, "rev-parse", "HEAD");
            var (kernel, goal) = CreateGoal(fixture.OldHead, true, true);
            var second = goal.CriterionEvidenceObligations[1];
            kernel.MapCriterionEvidenceOwner(
                goal.Id, 1, 1, CriterionEvidenceOwner.Acceptance, "reviewer",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: differentHead);
            kernel.RecordCriterionEvidence(
                goal.Id, second.Id, CriterionEvidenceOwner.Acceptance, differentHead,
                $"full-acceptance:{differentHead}:1", second.RequiredScope,
                passed: true, detail: "Different candidate full acceptance result");
            var before = goal.CriterionEvidenceObligations.ToArray();

            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Equal(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{before[0].Id}' is bound to {fixture.OldHead}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: reason=not-eligible.",
                diagnostic);
            Assert.Equal(before, goal.CriterionEvidenceObligations.ToArray());
            Assert.Empty(CarryNotes(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(string oldHead, params bool[] passed)
    {
        var (kernel, goal) = SimpleGoal("Carry satisfied acceptance evidence to a passing candidate");
        var criteria = passed.Select((_, index) => $"Full acceptance criterion {index}").ToArray();
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, criteria, VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < passed.Length; index++)
        {
            var obligation = kernel.MapCriterionEvidenceOwner(
                goal.Id, index, 1, CriterionEvidenceOwner.Acceptance, "reviewer",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: oldHead);
            kernel.RecordCriterionEvidence(
                goal.Id, obligation.Id, CriterionEvidenceOwner.Acceptance, oldHead,
                $"full-acceptance:{oldHead}:{index}", obligation.RequiredScope,
                passed: passed[index], detail: "Prior deterministic full acceptance result");
        }
        return (kernel, goal);
    }

    private static GoalOperationJournalEntry[] CarryNotes(string repository, Goal goal) =>
        GoalOperationJournal.Read(repository, goal.Id).Entries
            .Where(entry => entry.Operation == "conductor:criterion-evidence-satisfied-acceptance-carry")
            .ToArray();

    private static PatchFixture CreateFixture(PatchScenario scenario)
    {
        var repository = CreateGitRepository();
        var oldBase = ReadGit(repository, "rev-parse", "main");
        ReadGit(repository, "checkout", "-b", "candidate-old");
        AppendCommit(repository, "src/first.txt", "first");
        AppendCommit(repository, "src/second.txt", "second");
        var oldHead = ReadGit(repository, "rev-parse", "HEAD");
        ReadGit(repository, "branch", "old-candidate-anchor", oldHead);
        ReadGit(repository, "checkout", "main");
        AppendCommit(repository, "src/main-advance.txt", "new main");
        var mainHead = ReadGit(repository, "rev-parse", "main");
        if (scenario == PatchScenario.Equivalent)
        {
            ReadGit(repository, "checkout", "candidate-old");
            ReadGit(repository, "rebase", "--onto", "main", oldBase);
        }
        else
        {
            ReadGit(repository, "checkout", "-b", "candidate-new", "main");
            AppendCommit(repository, "src/first.txt", "first");
            AppendCommit(repository, "src/second.txt", scenario == PatchScenario.Edited ? "edited" : "second");
            if (scenario == PatchScenario.Added)
                AppendCommit(repository, "src/third.txt", "third");
        }
        var newHead = ReadGit(repository, "rev-parse", "HEAD");
        if (scenario == PatchScenario.Unresolvable)
            oldHead = new string('f', 40);
        return new PatchFixture(repository, oldHead, newHead, mainHead);
    }

    public enum PatchScenario { Equivalent, Edited, Added, Unresolvable }

    private sealed record PatchFixture(string Repository, string OldHead, string NewHead, string MainHead);
}
