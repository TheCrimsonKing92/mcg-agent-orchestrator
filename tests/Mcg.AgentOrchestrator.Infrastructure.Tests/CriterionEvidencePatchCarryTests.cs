using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;
using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class CriterionEvidencePatchCarryTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void PatchIdenticalRebaseCarriesAcceptanceEvidenceAndLands()
    {
        var fixture = CreatePatchFixture(PatchScenario.Equivalent);
        try
        {
            var result = Advance(fixture, OperatorEvidence.None);

            Assert.Equal(1, result.LandCalls);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Advance.Outcome);
            var acceptance = Assert.Single(result.Goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, acceptance.State);
            Assert.Equal(fixture.NewHead, acceptance.CandidateSha);
            Assert.Equal($"full-acceptance:{fixture.NewHead}", acceptance.ReceiptId);
            var carry = Assert.Single(
                GoalOperationJournal.Read(fixture.Repository, result.Goal.Id).Entries,
                entry => entry.Operation == "conductor:criterion-evidence-patch-carry");
            Assert.Contains(fixture.OldHead, carry.Detail, StringComparison.Ordinal);
            Assert.Contains(fixture.NewHead, carry.Detail, StringComparison.Ordinal);
            Assert.Contains("range-diff", carry.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(PatchScenario.Edited)]
    [Xunit.InlineData(PatchScenario.Added)]
    [Xunit.InlineData(PatchScenario.Dropped)]
    [Xunit.InlineData(PatchScenario.Reordered)]
    [Xunit.InlineData(PatchScenario.Unresolvable)]
    public void NonEquivalentOrUncomputableCandidateRefusesWithoutRebinding(PatchScenario scenario)
    {
        var fixture = CreatePatchFixture(scenario);
        try
        {
            var result = Advance(fixture, OperatorEvidence.None);

            Assert.Equal(0, result.LandCalls);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Advance.Outcome);
            var obligation = Assert.Single(result.Goal.CriterionEvidenceObligations);
            Assert.Equal(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{obligation.Id}' is bound to {fixture.OldHead}. Rebind the obligation to the current candidate before recording its evidence.",
                held.Reason);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(fixture.OldHead, obligation.ExpectedCandidateSha);
            Assert.DoesNotContain(
                GoalOperationJournal.Read(fixture.Repository, result.Goal.Id).Entries,
                entry => entry.Operation == "conductor:criterion-evidence-patch-carry");
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void PatchIdenticalRebaseCarriesSatisfiedOperatorReceipt()
    {
        var fixture = CreatePatchFixture(PatchScenario.Equivalent);
        try
        {
            var result = Advance(fixture, OperatorEvidence.Satisfied);

            Assert.Equal(1, result.LandCalls);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Advance.Outcome);
            var operatorObligation = Assert.Single(
                result.Goal.CriterionEvidenceObligations,
                item => item.Owner == CriterionEvidenceOwner.Operator);
            Assert.Equal(CriterionEvidenceState.Satisfied, operatorObligation.State);
            Assert.Equal(fixture.NewHead, operatorObligation.CandidateSha);
            Assert.Equal($"operator-old:carried:{fixture.NewHead}", operatorObligation.ReceiptId);
            var prior = Assert.Single(operatorObligation.PriorReceipts!);
            Assert.Equal(fixture.OldHead, prior.CandidateSha);
            Assert.Equal("operator-old", prior.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void PatchIdenticalRebaseDoesNotSatisfyPendingOperatorObligation()
    {
        var fixture = CreatePatchFixture(PatchScenario.Equivalent);
        try
        {
            var result = Advance(fixture, OperatorEvidence.Pending);

            Assert.Equal(0, result.LandCalls);
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Advance.Outcome);
            var operatorObligation = Assert.Single(
                result.Goal.CriterionEvidenceObligations,
                item => item.Owner == CriterionEvidenceOwner.Operator);
            Assert.Equal(result.OriginalOperatorObligation, operatorObligation);
            Assert.Equal(CriterionEvidenceState.Pending, operatorObligation.State);
            Assert.Equal(fixture.OldHead, operatorObligation.ExpectedCandidateSha);
            Assert.Null(operatorObligation.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    private static AdvanceResult Advance(PatchFixture fixture, OperatorEvidence operatorEvidence)
    {
        var (kernel, goal) = SimpleGoal("Carry criterion evidence across a patch-identical rebase");
        var criteria = operatorEvidence == OperatorEvidence.None
            ? new[] { "Full acceptance passes" }
            : new[] { "Full acceptance passes", "Operator observation remains valid" };
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective,
            criteria,
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            0,
            1,
            CriterionEvidenceOwner.Acceptance,
            "reviewer",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: fixture.OldHead);
        CriterionEvidenceObligation? originalOperatorObligation = null;
        if (operatorEvidence != OperatorEvidence.None)
        {
            originalOperatorObligation = kernel.MapCriterionEvidenceOwner(
                goal.Id,
                1,
                1,
                CriterionEvidenceOwner.Operator,
                "operator",
                "operator:native-observation",
                expectedCandidateSha: fixture.OldHead);
            if (operatorEvidence == OperatorEvidence.Satisfied)
            {
                originalOperatorObligation = kernel.RecordCriterionEvidence(
                    goal.Id,
                    originalOperatorObligation.Id,
                    CriterionEvidenceOwner.Operator,
                    fixture.OldHead,
                    "operator-old",
                    originalOperatorObligation.RequiredScope,
                    passed: true,
                    detail: "Observed on the original candidate");
            }
        }

        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [], BranchHeadSha: fixture.NewHead),
            resolveAcceptanceHeads: _ => (fixture.NewHead, fixture.MainHead),
            land: item =>
            {
                landCalls++;
                return new LandingResult(
                    item.Id.Value,
                    item.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            },
            executionDirectory: fixture.Repository);
        driver.BeginTick(kernel, 1);
        var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        return new AdvanceResult(goal, advance, landCalls, originalOperatorObligation);
    }

    private static PatchFixture CreatePatchFixture(PatchScenario scenario)
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
            if (scenario == PatchScenario.Reordered)
            {
                AppendCommit(repository, "src/second.txt", "second");
                AppendCommit(repository, "src/first.txt", "first");
            }
            else
            {
                AppendCommit(repository, "src/first.txt", "first");
                if (scenario != PatchScenario.Dropped)
                {
                    AppendCommit(repository, "src/second.txt", scenario == PatchScenario.Edited ? "edited" : "second");
                }
                if (scenario == PatchScenario.Added)
                {
                    AppendCommit(repository, "src/third.txt", "third");
                }
            }
        }

        var newHead = ReadGit(repository, "rev-parse", "HEAD");
        if (scenario == PatchScenario.Unresolvable)
        {
            oldHead = new string('f', 40);
        }
        return new PatchFixture(repository, oldHead, newHead, mainHead);
    }

    public enum PatchScenario
    {
        Equivalent,
        Edited,
        Added,
        Dropped,
        Reordered,
        Unresolvable
    }

    private enum OperatorEvidence
    {
        None,
        Pending,
        Satisfied
    }

    private sealed record PatchFixture(string Repository, string OldHead, string NewHead, string MainHead);
    private sealed record AdvanceResult(
        Goal Goal,
        ConductorAdvanceResult Advance,
        int LandCalls,
        CriterionEvidenceObligation? OriginalOperatorObligation);
}
