using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PatchEquivalenceIntegrationMergeTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void LinearRebaseDroppingCleanIntegrationMergeCarriesForward()
    {
        var fixture = CreateFixture(PatchScenario.Equivalent);
        try
        {
            Assert.True(
                GoalWorktrees.TryComputePatchEquivalence(
                    fixture.Repository, fixture.OldHead, fixture.NewHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out var evidence, out var refusal),
                refusal);
            Assert.Contains("range-diff", evidence, StringComparison.Ordinal);

            var (kernel, goal) = BindAcceptanceObligation(fixture.OldHead);
            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Null(diagnostic);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.NewHead, obligation.CandidateSha);
            Assert.Equal($"full-acceptance:{fixture.NewHead}", obligation.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(PatchScenario.Edited, "range-diff-not-identical")]
    [Xunit.InlineData(PatchScenario.Added, "commit-count-mismatch")]
    [Xunit.InlineData(PatchScenario.Dropped, "commit-count-mismatch")]
    public void IntegrationMergeOldHeadRefusesWhenGoalPatchesDiffer(PatchScenario scenario, string reason)
    {
        var fixture = CreateFixture(scenario);
        try
        {
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out _, out var refusal));
            Assert.Equal(reason, refusal);

            var (kernel, goal) = BindAcceptanceObligation(fixture.OldHead);
            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.StartsWith(
                $"Acceptance passed for {fixture.NewHead}, but obligation '{obligation.Id}' is bound to {fixture.OldHead}.",
                diagnostic);
            Assert.Contains($"reason={reason}", diagnostic, StringComparison.Ordinal);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(fixture.OldHead, obligation.ExpectedCandidateSha);
            Assert.Null(obligation.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void DirtyIntegrationMergeRefusesWithDistinctReasonNamingMerge(bool dirtyOldHead)
    {
        var fixture = CreateFixture(
            PatchScenario.Equivalent, dirtyOld: dirtyOldHead, dirtyNew: !dirtyOldHead);
        try
        {
            var dirtyMerge = dirtyOldHead ? fixture.OldMerge : fixture.NewMerge;
            var expectedReason = $"integration-merge-not-clean; merge={dirtyMerge}";
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, out _, out var refusal));
            Assert.Equal(expectedReason, refusal);

            var (kernel, goal) = BindAcceptanceObligation(fixture.OldHead);
            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, fixture.Repository);

            Assert.Contains($"reason={expectedReason}", diagnostic, StringComparison.Ordinal);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(fixture.OldHead, obligation.ExpectedCandidateSha);
            Assert.Null(obligation.ReceiptId);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) BindAcceptanceObligation(string oldHead)
    {
        var (kernel, goal) = SimpleGoal("Carry acceptance evidence across a clean integration merge");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(
            goal.Id,
            0,
            1,
            CriterionEvidenceOwner.Acceptance,
            "reviewer",
            CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: oldHead);
        return (kernel, goal);
    }

    private static PatchFixture CreateFixture(
        PatchScenario scenario,
        bool dirtyOld = false,
        bool dirtyNew = false)
    {
        var repository = CreateGitRepository();
        var baseSha = ReadGit(repository, "rev-parse", "main");
        ReadGit(repository, "checkout", "-b", "candidate-old");
        AppendCommit(repository, "src/first.txt", "first");
        ReadGit(repository, "checkout", "main");
        AppendCommit(repository, "src/main-mid.txt", "mid");
        ReadGit(repository, "checkout", "candidate-old");
        var oldMerge = MergeMain(repository, dirtyOld);
        AppendCommit(repository, "src/second.txt", "second");
        var oldHead = ReadGit(repository, "rev-parse", "HEAD");
        ReadGit(repository, "branch", "old-candidate-anchor", oldHead);

        ReadGit(repository, "checkout", "main");
        AppendCommit(repository, "src/main-new.txt", "new");

        string? newMerge = null;
        if (dirtyNew)
        {
            ReadGit(repository, "checkout", "-b", "candidate-new", baseSha);
            AppendCommit(repository, "src/first.txt", "first");
            newMerge = MergeMain(repository, dirty: true);
            AppendCommit(repository, "src/second.txt", "second");
        }
        else if (scenario == PatchScenario.Equivalent)
        {
            ReadGit(repository, "checkout", "-b", "candidate-new", oldHead);
            ReadGit(repository, "rebase", "main");
        }
        else
        {
            ReadGit(repository, "checkout", "-b", "candidate-new", "main");
            AppendCommit(repository, "src/first.txt", "first");
            if (scenario != PatchScenario.Dropped)
                AppendCommit(repository, "src/second.txt", scenario == PatchScenario.Edited ? "edited" : "second");
            if (scenario == PatchScenario.Added)
                AppendCommit(repository, "src/third.txt", "third");
        }

        return new PatchFixture(repository, oldHead, ReadGit(repository, "rev-parse", "HEAD"), oldMerge, newMerge);
    }

    private static string MergeMain(string repository, bool dirty)
    {
        if (dirty)
        {
            ReadGit(repository, "merge", "--no-ff", "--no-commit", "main");
            var path = Path.Combine(repository, "src", "evil.txt");
            File.WriteAllText(path, "manual merge content" + Environment.NewLine);
            ReadGit(repository, "add", "src/evil.txt");
            ReadGit(repository, "commit", "-m", "Integrate main with extra content");
        }
        else
        {
            ReadGit(repository, "merge", "--no-ff", "main", "-m", "Integrate main into goal");
        }

        return ReadGit(repository, "rev-parse", "HEAD");
    }

    public enum PatchScenario { Equivalent, Edited, Added, Dropped }

    private sealed record PatchFixture(
        string Repository,
        string OldHead,
        string NewHead,
        string OldMerge,
        string? NewMerge);
}
