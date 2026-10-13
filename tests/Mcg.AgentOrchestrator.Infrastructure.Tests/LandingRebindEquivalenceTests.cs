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
                twinGoal, fixture.NewHead, twinKernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, "test", Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

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
                goal, fixture.NewHead, kernel, "test", Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository));

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
                goal, fixture.NewHead, kernel, "test", Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository));
            Assert.Equal(CriterionEvidenceState.Satisfied, Assert.Single(goal.CriterionEvidenceObligations).State);
            Assert.Empty(LandingCarryEntries(fixture.Repository, goal));
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false, true, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, true, true, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false, false, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false, true, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false, true, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, true, true, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false, true, false)]
    public void RebindEvidence_ProvenReceiptsCarryAndUnprovenBindingsRemain(
        CriterionEvidenceOwner owner, bool changedGoalLine, bool satisfied, bool hasExecutionDirectory)
    {
        var fixture = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(changedGoalLine);
        try
        {
            var (kernel, goal) = SimpleGoal("Preserve reviewed evidence across landing rebind");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective, ["Full acceptance passes", "Prior evidence remains valid"],
                VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: fixture.NewHead);
            var scope = owner == CriterionEvidenceOwner.Acceptance
                ? CriterionEvidenceScopes.FullAcceptanceGate : "operator observation";
            var original = kernel.MapCriterionEvidenceOwner(goal.Id, 1, 1, owner,
                "test", scope, expectedCandidateSha: fixture.OldHead);
            if (satisfied)
                original = kernel.RecordCriterionEvidence(goal.Id, original.Id, owner,
                    fixture.OldHead, "old-receipt", scope, true, "Observed on the original candidate");

            var diagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, "test", TrunkBranchName.Default,
                hasExecutionDirectory ? fixture.Repository : null);

            var evidence = Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == original.Id);
            var notes = GoalOperationJournal.Read(fixture.Repository, goal.Id).Entries
                .Where(entry => entry.Operation == "conductor:criterion-evidence-patch-carry").ToArray();
            if (satisfied && !changedGoalLine && hasExecutionDirectory)
            {
                Assert.Null(diagnostic);
                Assert.Equal(CriterionEvidenceState.Satisfied, evidence.State);
                Assert.Equal(fixture.NewHead, evidence.ExpectedCandidateSha);
                Assert.Equal(fixture.NewHead, evidence.CandidateSha);
                Assert.Equal(owner == CriterionEvidenceOwner.Operator
                    ? $"old-receipt:carried:{fixture.NewHead}" : $"full-acceptance:{fixture.NewHead}", evidence.ReceiptId);
                var prior = Assert.Single(evidence.PriorReceipts!);
                Assert.Equal(fixture.OldHead, prior.CandidateSha);
                Assert.Equal("old-receipt", prior.ReceiptId);
                var note = Assert.Single(notes);
                Assert.Contains(fixture.OldHead, note.Detail, StringComparison.Ordinal);
                Assert.Contains(fixture.NewHead, note.Detail, StringComparison.Ordinal);
                Assert.Contains("goal-owned lines", note.Detail, StringComparison.Ordinal);
            }
            else
            {
                Assert.NotNull(diagnostic);
                Assert.Equal(original, evidence);
                Assert.Empty(notes);
                if (owner == CriterionEvidenceOwner.Operator && satisfied && !hasExecutionDirectory)
                    Assert.Contains("reason=no-execution-directory", diagnostic, StringComparison.Ordinal);
            }
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
