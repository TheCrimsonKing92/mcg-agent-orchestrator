using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class LandingRebindEquivalenceTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void ChangedGoalLineLeavesBindingPendingWithExistingRefusalText()
    {
        var fixture = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(changeGoalLine: true);
        try
        {
            var (kernel, goal) = BindAcceptance(fixture.OldHead);
            var (twinKernel, twinGoal) = BindAcceptance(fixture.OldHead);
            var expected = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                twinGoal, fixture.NewHead, twinKernel, fixture.Repository);

            var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, "test", fixture.Repository);

            Assert.Equal(expected, diagnostic);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.StartsWith(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{obligation.Id}' is bound to {fixture.OldHead}.",
                diagnostic);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(fixture.OldHead, obligation.ExpectedCandidateSha);
            Assert.Null(obligation.ReceiptId);
            Assert.Empty(LandingCarryEntries(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void EquivalentGoalLinesRebindAndWriteOneTimelineNote()
    {
        var fixture = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(changeGoalLine: false);
        try
        {
            var (kernel, goal) = BindAcceptance(fixture.OldHead);
            Assert.Null(AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, "test", fixture.Repository));

            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.NewHead, obligation.ExpectedCandidateSha);
            Assert.Equal(fixture.NewHead, obligation.CandidateSha);
            Assert.Equal($"full-acceptance:{fixture.NewHead}", obligation.ReceiptId);
            var note = Assert.Single(LandingCarryEntries(fixture.Repository, goal));
            Assert.Contains(obligation.Id, note.Detail, StringComparison.Ordinal);
            Assert.Contains(fixture.OldHead, note.Detail, StringComparison.Ordinal);
            Assert.Contains(fixture.NewHead, note.Detail, StringComparison.Ordinal);
            Assert.Contains("goal-owned lines", note.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void AlreadyBoundCandidateDoesNotWriteLandingCarryNote()
    {
        var fixture = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(changeGoalLine: false);
        try
        {
            var (kernel, goal) = BindAcceptance(fixture.NewHead);
            Assert.Null(AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, "test", fixture.Repository));
            Assert.Equal(CriterionEvidenceState.Satisfied, Assert.Single(goal.CriterionEvidenceObligations).State);
            Assert.Empty(LandingCarryEntries(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    private static GoalOperationJournalEntry[] LandingCarryEntries(string repository, Goal goal) =>
        GoalOperationJournal.Read(repository, goal.Id).Entries
            .Where(entry => entry.Operation == "conductor:criterion-evidence-landing-carry")
            .ToArray();

    private static (AgentOrchestratorKernel Kernel, Goal Goal) BindAcceptance(string head)
    {
        var (kernel, goal) = SimpleGoal("Bind full acceptance evidence to reviewed goal lines");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: head);
        return (kernel, goal);
    }
}
