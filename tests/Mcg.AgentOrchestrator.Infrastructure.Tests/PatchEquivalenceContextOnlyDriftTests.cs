using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static LandingExecutorTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PatchEquivalenceContextOnlyDriftTests : HostCapacityBoundTestBase
{
    [Xunit.Fact]
    public void ContextOnlyDriftCarriesAcceptanceObligation()
    {
        var fixture = CreateFixture(changeGoalLineAfterRebase: false);
        try
        {
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead, out var evidence, out var refusal), refusal);
            Assert.Contains("zero-context comparison", evidence, StringComparison.Ordinal);

            var (kernel, goal) = BindAcceptance(fixture.OldHead);
            Assert.Null(AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, fixture.Repository));
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal(fixture.NewHead, obligation.ExpectedCandidateSha);
            Assert.Contains(GoalOperationJournal.Read(fixture.Repository, goal.Id).Entries,
                entry => entry.Operation == "conductor:criterion-evidence-patch-carry");
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    [Xunit.Fact]
    public void ChangedGoalLineStillRefusesCarry()
    {
        var fixture = CreateFixture(changeGoalLineAfterRebase: true);
        try
        {
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(
                fixture.Repository, fixture.OldHead, fixture.NewHead, out _, out var refusal));
            Assert.Equal("range-diff-not-identical", refusal);

            var (kernel, goal) = BindAcceptance(fixture.OldHead);
            var diagnostic = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(
                goal, fixture.NewHead, kernel, fixture.Repository);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            Assert.StartsWith($"Acceptance passed for {fixture.NewHead}, but obligation '{obligation.Id}' is bound to {fixture.OldHead}.", diagnostic);
            Assert.Contains("reason=range-diff-not-identical", diagnostic, StringComparison.Ordinal);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Equal(fixture.OldHead, obligation.ExpectedCandidateSha);
            Assert.Null(obligation.ReceiptId);
            Assert.DoesNotContain(GoalOperationJournal.Read(fixture.Repository, goal.Id).Entries,
                entry => entry.Operation == "conductor:criterion-evidence-patch-carry");
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) BindAcceptance(string oldHead)
    {
        var (kernel, goal) = SimpleGoal("Carry context-only rebased acceptance evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: oldHead);
        return (kernel, goal);
    }

    private static (string Repository, string OldHead, string NewHead) CreateFixture(bool changeGoalLineAfterRebase)
    {
        var repo = CreateGitRepository();
        var file = Path.Combine(repo, "src", "adjacent.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var lines = Enumerable.Range(1, 20).Select(index => $"line {index}").ToArray();
        File.WriteAllLines(file, lines);
        ReadGit(repo, "add", "src/adjacent.txt");
        ReadGit(repo, "commit", "-m", "Add adjacent-line fixture");
        var oldBase = ReadGit(repo, "rev-parse", "main");

        ReadGit(repo, "checkout", "-b", "candidate-old");
        lines[9] = "goal line ten";
        File.WriteAllLines(file, lines);
        ReadGit(repo, "add", "src/adjacent.txt");
        ReadGit(repo, "commit", "-m", "Edit goal line");
        var oldHead = ReadGit(repo, "rev-parse", "HEAD");
        ReadGit(repo, "branch", "old-candidate-anchor", oldHead);

        ReadGit(repo, "checkout", "main");
        lines[9] = "line 10";
        lines[12] = "main line thirteen";
        File.WriteAllLines(file, lines);
        ReadGit(repo, "add", "src/adjacent.txt");
        ReadGit(repo, "commit", "-m", "Edit adjacent main line");
        ReadGit(repo, "checkout", "candidate-old");
        var rebase = GitCli.Run(repo, "rebase", "--onto", "main", oldBase);
        if (!rebase.Succeeded)
        {
            // Adjacent edits may conflict under a Git version's merge strategy. Resolve
            // the conflict to the same two nonoverlapping lines before continuing.
            lines[9] = "goal line ten";
            File.WriteAllLines(file, lines);
            ReadGit(repo, "add", "src/adjacent.txt");
            ReadGit(repo, "-c", "core.editor=true", "rebase", "--continue");
        }
        if (changeGoalLineAfterRebase)
        {
            lines[9] = "changed goal line ten";
            File.WriteAllLines(file, lines);
            ReadGit(repo, "add", "src/adjacent.txt");
            ReadGit(repo, "commit", "--amend", "--no-edit");
        }
        return (repo, oldHead, ReadGit(repo, "rev-parse", "HEAD"));
    }
}
