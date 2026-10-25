using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

// Each test owns its Git repository, worktree, kernel, journal and inline coordinator.
public sealed class PassedHeadObligationRebindTests : ConductorBatchLoopTests
{
    private const string ConflictPath = "src/conflict.cs";
    private const string GoalPath = "src/goal.txt";
    private const string GuardedPath = "src/guarded.cs";

    public PassedHeadObligationRebindTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(DisplayName = "A verified conflict merge rebinds both obligations and lands once")]
    public void PassedCurrentHead_RebindsAndLands()
    {
        using var fixture = CreateFixture();
        AssertExistingProofsRefuse(fixture);
        Assert.True(Prove(fixture, fixture.Head, out var evidence));
        Assert.Equal($"passed-at-head: bound={fixture.Bound} head={fixture.Head} walked={fixture.Head}", evidence);

        var result = RunLoop(fixture);
        Assert.Equal(1, result.AcceptanceRuns);
        Assert.Equal(1, result.LandCalls);
        Assert.Equal(0, result.Summary.Held);
        AssertSatisfiedAndJournaled(fixture);

        Assert.Null(AcceptanceCriterionEvidence.RecordAndCreateHold(fixture.Goal, fixture.Head,
            fixture.Kernel, "main", fixture.Repository, currentHeadSha: fixture.Head));
        AssertSatisfiedAndJournaled(fixture); // Replaying the pass does not duplicate the entry.
    }

    [Xunit.Theory(DisplayName = "Changed ratchet bases permit a lower merged ceiling and refuse a raise")]
    [Xunit.InlineData(22, true)]
    [Xunit.InlineData(26, false)]
    public void RatchetBaseChangedOnBothSides_RequiresNonIncreasingCeiling(int ceiling, bool lands)
    {
        using var fixture = CreateFixture(ceiling);
        AssertExistingProofsRefuse(fixture);
        var result = RunLoop(fixture);
        Assert.Equal(1, result.AcceptanceRuns);
        Assert.Equal(lands ? 1 : 0, result.LandCalls);
        Assert.Equal(lands ? 0 : 1, result.Summary.Held);
        if (lands) AssertSatisfiedAndJournaled(fixture);
        else AssertPendingWithoutRebind(fixture);
    }

    [Xunit.Fact(DisplayName = "A pass on an older candidate preserves the baseline hold")]
    public void OlderVerifiedCandidate_KeepsBaselineHold()
    {
        using var fixture = CreateFixture();
        AppendCommit(fixture.Worktree, GoalPath, "new goal content");
        var currentHead = ReadGit(fixture.Worktree, "rev-parse", "HEAD");
        var (baselineKernel, baselineGoal) = CreateBoundGoal(fixture.Bound);
        var baseline = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(baselineGoal, fixture.Head,
            baselineKernel, "main", fixture.Repository);

        var held = AcceptanceCriterionEvidence.RecordAndCreateHold(fixture.Goal, fixture.Head,
            fixture.Kernel, "main", fixture.Repository, currentHeadSha: currentHead);
        Assert.NotNull(baseline);
        Assert.NotNull(held);
        Assert.Equal(baseline, held.Reason);
        AssertPendingWithoutRebind(fixture);
    }

    [Xunit.Fact(DisplayName = "A bound commit outside head ancestry cannot prove drift")]
    public void NonAncestorBound_Refuses()
    {
        using var fixture = CreateFixture();
        Assert.True(Prove(fixture, fixture.Head, out _));
        AppendCommit(fixture.Repository, "src/sibling.txt", "sibling content");
        var sibling = ReadGit(fixture.Repository, "rev-parse", "HEAD");
        Assert.Equal(1, GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor", sibling, fixture.Head).ExitCode);
        Assert.False(PassedHeadCandidateDrift.TryProve(fixture.Repository, sibling, fixture.Head, "main", out _));
    }

    [Xunit.Fact(DisplayName = "A merge from outside integration ancestry cannot prove drift")]
    public void ForeignMergeParent_Refuses()
    {
        using var fixture = CreateFixture();
        Assert.True(Prove(fixture, fixture.Head, out _));
        ReadGit(fixture.Repository, "checkout", "-b", "foreign", fixture.Bound);
        AppendCommit(fixture.Repository, "src/foreign.txt", "foreign content");
        var foreign = ReadGit(fixture.Repository, "rev-parse", "HEAD");
        ReadGit(fixture.Repository, "checkout", "main");
        ReadGit(fixture.Worktree, "merge", "--no-ff", "-m", "Merge foreign branch", "foreign");
        var head = ReadGit(fixture.Worktree, "rev-parse", "HEAD");
        Assert.Equal(1, GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor", foreign, "main").ExitCode);
        Assert.False(Prove(fixture, head, out _));
    }

    [Xunit.Fact(DisplayName = "A one-parent goal content change cannot prove drift")]
    public void GoalContentCommit_Refuses()
    {
        using var fixture = CreateFixture();
        Assert.True(Prove(fixture, fixture.Head, out _));
        AppendCommit(fixture.Worktree, GoalPath, "changed goal line");
        Assert.False(Prove(fixture, ReadGit(fixture.Worktree, "rev-parse", "HEAD"), out _));
    }

    [Xunit.Theory(DisplayName = "Ratchet-only commits need non-increasing numbers regardless of subject")]
    [Xunit.InlineData(21, true)]
    [Xunit.InlineData(23, false)]
    public void RatchetOnlyCommit_RequiresNonIncreasingNumbers(int ceiling, bool expected)
    {
        using var fixture = CreateFixture();
        Assert.True(Prove(fixture, fixture.Head, out _));
        WriteAuthority(fixture.Worktree, ceiling);
        ReadGit(fixture.Worktree, "add", SourceSizeRatchet.SourcePath);
        ReadGit(fixture.Worktree, "commit", "-m", "Adjust numeric ceiling");
        var head = ReadGit(fixture.Worktree, "rev-parse", "HEAD");
        Assert.Equal(expected, Prove(fixture, head, out var evidence));
        if (expected)
            Assert.Equal($"passed-at-head: bound={fixture.Bound} head={head} walked={head},{fixture.Head}", evidence);
    }

    [Xunit.Theory(DisplayName = "Satisfied evidence at the bound head disqualifies the binding group")]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void SatisfiedBindingAtBound_KeepsBaselineHold(bool operatorOwned)
    {
        using var fixture = CreateFixture(extraSatisfiedBinding: true, operatorOwned: operatorOwned);
        Assert.True(Prove(fixture, fixture.Head, out _));
        var (baselineKernel, baselineGoal) = CreateBoundGoal(fixture.Bound, true, operatorOwned);
        var baseline = AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(baselineGoal, fixture.Head,
            baselineKernel, "main", fixture.Repository);
        var held = AcceptanceCriterionEvidence.RecordAndCreateHold(fixture.Goal, fixture.Head,
            fixture.Kernel, "main", fixture.Repository, currentHeadSha: fixture.Head);
        Assert.NotNull(baseline);
        Assert.NotNull(held);
        Assert.Equal(baseline, held.Reason);
        AssertPendingWithoutRebind(fixture);
        var satisfied = Assert.Single(fixture.Goal.CriterionEvidenceObligations.Where(item => item.CriterionIndex == 2));
        Assert.Equal(fixture.Bound, satisfied.ExpectedCandidateSha);
        Assert.True(satisfied.HasSatisfiedEvidenceFor(fixture.Bound));
    }

    private static bool Prove(Fixture fixture, string head, out string evidence) =>
        PassedHeadCandidateDrift.TryProve(fixture.Repository, fixture.Bound, head, "main", out evidence);

    private static void AssertExistingProofsRefuse(Fixture fixture)
    {
        Assert.False(GoalWorktrees.TryComputePatchEquivalence(fixture.Repository, fixture.Bound,
            fixture.Head, "main", out _, out var reason));
        Assert.StartsWith("integration-merge-not-clean", reason, StringComparison.Ordinal);
        Assert.False(IntegrationOnlyCandidateDrift.TryProve(fixture.Repository, fixture.Bound,
            fixture.Head, "main", GoalWorktrees.BranchName(fixture.Goal.Id), out _));
    }

    private static void AssertSatisfiedAndJournaled(Fixture fixture)
    {
        Assert.Equal(2, fixture.Goal.CriterionEvidenceObligations.Count);
        Assert.All(fixture.Goal.CriterionEvidenceObligations, item =>
        {
            Assert.Equal(fixture.Head, item.ExpectedCandidateSha);
            Assert.True(item.HasSatisfiedEvidenceFor(fixture.Head));
        });
        Assert.Equal($"OBLIGATION_AUTO_REBIND goal={fixture.Goal.Id.Value} from={fixture.Bound} to={fixture.Head} reason=passed-at-head",
            Assert.Single(AutoRebindEntries(fixture)));
    }

    private static void AssertPendingWithoutRebind(Fixture fixture)
    {
        var pending = fixture.Goal.CriterionEvidenceObligations.Where(item => item.CriterionIndex < 2).ToArray();
        Assert.Equal(2, pending.Length);
        Assert.All(pending, item =>
        {
            Assert.Equal(fixture.Bound, item.ExpectedCandidateSha);
            Assert.Equal(CriterionEvidenceState.Pending, item.State);
        });
        Assert.Empty(AutoRebindEntries(fixture));
    }

    private static string[] AutoRebindEntries(Fixture fixture) =>
        GoalOperationJournal.ReadActive(fixture.Repository, fixture.Goal.Id).Entries
            .Where(entry => entry.Operation == "conductor:obligation-auto-rebind")
            .Select(entry => entry.Detail).ToArray();

    private static (BatchLoopSummary Summary, int LandCalls, int AcceptanceRuns) RunLoop(Fixture fixture)
    {
        var landCalls = 0;
        var acceptanceRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return new AcceptanceVerificationSummary(true, [], BranchHeadSha: fixture.Head);
            },
            resolveAcceptanceHeads: _ => (fixture.Head, ReadGit(fixture.Repository, "rev-parse", "main")),
            getLandingFileScopes: _ => [ConflictPath, GoalPath, SourceSizeRatchet.SourcePath],
            land: item =>
            {
                landCalls++;
                return new LandingResult(item.Id.Value, item.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            executionDirectory: fixture.Repository,
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(fixture.Repository, ".orchestrator", "test-attempts"), "main", fixture.Repository, runInline: true));
        var summary = new ConductorBatchLoop().Run(fixture.Kernel, driver,
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        return (summary, landCalls, acceptanceRuns);
    }

    private static Fixture CreateFixture(int mergedCeiling = 22, bool extraSatisfiedBinding = false, bool operatorOwned = true)
    {
        var repository = CreateGitRepository();
        try
        {
            ReadGit(repository, "config", "core.autocrlf", "false");
            Directory.CreateDirectory(Path.Combine(repository, "src"));
            File.WriteAllText(Path.Combine(repository, ConflictPath), "// seed\n");
            File.WriteAllText(Path.Combine(repository, GuardedPath), "// guarded\n");
            WriteAuthority(repository, 20);
            ReadGit(repository, "add", ConflictPath, GuardedPath, SourceSizeRatchet.SourcePath);
            ReadGit(repository, "commit", "-m", "Seed conflict and ratchet authority");
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Preserve goal changes across integration");
            var worktree = GoalWorktrees.Ensure(repository, goal.Id);
            File.AppendAllText(Path.Combine(worktree, ConflictPath), "// goal\n");
            File.WriteAllText(Path.Combine(worktree, GoalPath), "goal owned line\n");
            WriteAuthority(worktree, 24);
            ReadGit(worktree, "add", ConflictPath, GoalPath, SourceSizeRatchet.SourcePath);
            ReadGit(worktree, "commit", "-m", "Goal changes");
            var bound = ReadGit(worktree, "rev-parse", "HEAD");
            BindGoal(kernel, goal, bound, extraSatisfiedBinding, operatorOwned);

            File.AppendAllText(Path.Combine(repository, ConflictPath), "// integration\n");
            WriteAuthority(repository, 18);
            ReadGit(repository, "add", ConflictPath, SourceSizeRatchet.SourcePath);
            ReadGit(repository, "commit", "-m", "Integration changes");
            var merge = GitCli.Run(worktree, "merge", "--no-ff", "--no-commit", "main");
            Assert.False(merge.DrainTimedOut);
            Assert.Equal(1, merge.ExitCode);
            Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(worktree, ConflictPath)), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(worktree, ConflictPath), "// seed\n// integration\n// goal\n");
            WriteAuthority(worktree, mergedCeiling);
            ReadGit(worktree, "add", ConflictPath, SourceSizeRatchet.SourcePath);
            ReadGit(worktree, "commit", "-m", $"Integrate main into {GoalWorktrees.BranchName(goal.Id)} (additive conflict auto-merge)");
            var head = ReadGit(worktree, "rev-parse", "HEAD");
            return new Fixture(repository, worktree, kernel, goal, bound, head);
        }
        catch { TryDeleteDirectory(repository); throw; }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateBoundGoal(
        string bound, bool extraSatisfiedBinding = false, bool operatorOwned = true)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Preserve goal changes across integration");
        BindGoal(kernel, goal, bound, extraSatisfiedBinding, operatorOwned);
        return (kernel, goal);
    }

    private static void BindGoal(AgentOrchestratorKernel kernel, Goal goal, string bound,
        bool extraSatisfiedBinding, bool operatorOwned)
    {
        string[] criteria = extraSatisfiedBinding
            ? ["Full acceptance passes", "Goal content preserved", "Additional evidence"]
            : ["Full acceptance passes", "Goal content preserved"];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, criteria, VerificationClass.TestVerifiable, [], [])
        {
            OperatorOwnedAcceptanceCriteria = extraSatisfiedBinding && operatorOwned ? [criteria[2]] : []
        });
        for (var index = 0; index < 2; index++)
            kernel.MapCriterionEvidenceOwner(goal.Id, index, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: bound);
        if (!extraSatisfiedBinding) return;
        var owner = operatorOwned ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance;
        var scope = operatorOwned ? "operator:native-observation" : CriterionEvidenceScopes.FullAcceptanceGate;
        var obligation = kernel.MapCriterionEvidenceOwner(goal.Id, 2, 1, owner, "reviewer", scope, expectedCandidateSha: bound);
        kernel.RecordCriterionEvidence(goal.Id, obligation.Id, owner, bound, "bound-evidence", scope,
            passed: true, detail: "Observed at bound candidate");
    }

    private static void WriteAuthority(string root, int ceiling)
    {
        var path = Path.Combine(root, SourceSizeRatchet.SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"// authority comment\nnew SourceSizeCeiling(\"{GuardedPath}\", {ceiling}), // row comment\n");
    }

    private sealed class Fixture(string repository, string worktree, AgentOrchestratorKernel kernel,
        Goal goal, string bound, string head) : IDisposable
    {
        public string Repository { get; } = repository;
        public string Worktree { get; } = worktree;
        public AgentOrchestratorKernel Kernel { get; } = kernel;
        public Goal Goal { get; } = goal;
        public string Bound { get; } = bound;
        public string Head { get; } = head;
        public void Dispose() => TryDeleteDirectory(Repository);
    }
}
