using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

// Real Git repositories and an inline acceptance coordinator, isolated to each fixture root.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PatchEquivalenceIntegrationMergeTestsAutoRebind : ConductorBatchLoopTests
{
    private const string GuardedPath = "src/guarded.cs";
    private const string GoalPath = "src/goal.txt";
    private const string MergeSubjectPrefix = "Integrate main into ";

    public PatchEquivalenceIntegrationMergeTestsAutoRebind(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void IntegrationOnlyChain_LandsInOnePass(bool satisfied)
    {
        using var fixture = CreateFixture();
        if (satisfied)
            foreach (var obligation in fixture.Goal.CriterionEvidenceObligations.ToArray())
                fixture.Kernel.RecordCriterionEvidence(fixture.Goal.Id, obligation.Id,
                    CriterionEvidenceOwner.Acceptance, fixture.Bound, $"old:{obligation.Id}",
                    CriterionEvidenceScopes.FullAcceptanceGate, passed: true, detail: "Passed bound candidate");

        Assert.False(GoalWorktrees.TryComputePatchEquivalence(fixture.Repository, fixture.Bound,
            fixture.Candidate, "main", out _, out var reason));
        Assert.Equal("commit-count-mismatch", reason);
        var result = RunLoop(fixture);
        Assert.Equal(1, result.LandCalls);
        Assert.Equal(1, result.AcceptanceRuns);
        Assert.Equal(0, result.Summary.Held);
        AssertSatisfied(fixture);
        AssertAutoRebind(fixture);
    }

    [Xunit.Fact]
    public void MergeTrainEntry_IntegrationOnlyChain_RebindsAndRecords()
    {
        using var fixture = CreateFixture();
        Assert.Null(AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            fixture.Goal, fixture.Candidate, "main", fixture.Repository));
        Assert.Empty(AutoRebindEntries(fixture)); // The planning entry is read-only.
        Assert.All(fixture.Goal.CriterionEvidenceObligations, item => Assert.Equal(fixture.Bound, item.ExpectedCandidateSha));

        Assert.Null(AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
            fixture.Goal, fixture.Candidate, fixture.Kernel, "test-full-acceptance", "main", fixture.Repository));
        AssertSatisfied(fixture);
        AssertAutoRebind(fixture);
        Assert.Null(AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            fixture.Goal, fixture.Candidate, "main", fixture.Repository));
        Assert.Null(AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
            fixture.Goal, fixture.Candidate, fixture.Kernel, "test-full-acceptance", "main", fixture.Repository));
        AssertAutoRebind(fixture); // Replaying the same candidate does not duplicate the journal entry.
    }

    [Xunit.Fact]
    public void GoalFollowUpCommit_KeepsBaselineHold()
    {
        using var fixture = CreateFixture();
        ReadGit(fixture.Worktree, "commit", "--allow-empty", "-m", "Goal follow-up");
        fixture.Candidate = ReadGit(fixture.Worktree, "rev-parse", "HEAD");
        AssertBaselineHold(fixture);
    }

    [Xunit.Fact]
    public void IntegrationMergeEditingGoalLine_KeepsBaselineHold()
    {
        using var fixture = CreateFixture(secondMerge: "goal-edit");
        AssertBaselineHold(fixture);
    }

    [Xunit.Fact]
    public void RaisedRatchetCeiling_KeepsBaselineHold()
    {
        using var fixture = CreateFixture(secondMerge: "raise");
        AssertBaselineHold(fixture);
    }

    [Xunit.Theory]
    [Xunit.InlineData("foreign")]
    [Xunit.InlineData("dispatch")]
    public void TryProve_WrongIntegrationSubject_Refuses(string shape)
    {
        using var fixture = CreateFixture(secondMerge: shape);
        Assert.False(Prove(fixture, out _));
    }

    [Xunit.Fact]
    public void TryProve_NonAncestorBound_Refuses()
    {
        using var fixture = CreateFixture();
        AppendCommit(fixture.Repository, "sibling.txt", "not in candidate ancestry");
        var sibling = ReadGit(fixture.Repository, "rev-parse", "HEAD");
        Assert.Equal(1, GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor", sibling, fixture.Candidate).ExitCode);
        Assert.False(IntegrationOnlyCandidateDrift.TryProve(fixture.Repository, sibling,
            fixture.Candidate, "main", GoalWorktrees.BranchName(fixture.Goal.Id), out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData(20, true)]
    [Xunit.InlineData(19, true)]
    [Xunit.InlineData(17, false)]
    public void TryProve_NetRatchetRows_RequireNonIncreasingCeilings(int boundCeiling, bool expected)
    {
        using var fixture = CreateFixture(boundCeiling: boundCeiling);
        Assert.Equal(expected, Prove(fixture, out var evidence));
        if (!expected) return;
        Assert.StartsWith("integration-only:", evidence, StringComparison.Ordinal);
        Assert.Equal(3, fixture.Walked.Length);
        Assert.All(fixture.Walked, sha => Assert.Contains(sha, evidence, StringComparison.Ordinal));
    }

    private static bool Prove(Fixture fixture, out string evidence) =>
        IntegrationOnlyCandidateDrift.TryProve(fixture.Repository, fixture.Bound, fixture.Candidate,
            "main", GoalWorktrees.BranchName(fixture.Goal.Id), out evidence);

    private static void AssertBaselineHold(Fixture fixture)
    {
        Assert.False(GoalWorktrees.TryComputePatchEquivalence(fixture.Repository, fixture.Bound,
            fixture.Candidate, "main", out _, out var refusal));
        Assert.False(Prove(fixture, out _));
        var result = RunLoop(fixture);
        Assert.Equal(0, result.LandCalls);
        Assert.Equal(1, result.Summary.Held);
        var held = AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            fixture.Goal, fixture.Candidate, "main", fixture.Repository);
        Assert.NotNull(held);
        Assert.Contains($"reason={refusal}", held.Reason, StringComparison.Ordinal);
        Assert.All(fixture.Goal.CriterionEvidenceObligations, item =>
        {
            Assert.Equal(fixture.Bound, item.ExpectedCandidateSha);
            Assert.Equal(CriterionEvidenceState.Pending, item.State);
        });
        Assert.Empty(AutoRebindEntries(fixture));
    }

    private static void AssertSatisfied(Fixture fixture)
    {
        Assert.Equal(2, fixture.Goal.CriterionEvidenceObligations.Count);
        Assert.All(fixture.Goal.CriterionEvidenceObligations, item =>
        {
            Assert.Equal(fixture.Candidate, item.ExpectedCandidateSha);
            Assert.True(item.HasSatisfiedEvidenceFor(fixture.Candidate));
        });
        Assert.Empty(fixture.Goal.GetOutstandingCriterionEvidenceObligations(fixture.Candidate));
    }

    private static string[] AutoRebindEntries(Fixture fixture) =>
        GoalOperationJournal.ReadActive(fixture.Repository, fixture.Goal.Id).Entries
            .Where(entry => entry.Operation == "conductor:obligation-auto-rebind")
            .Select(entry => entry.Detail).ToArray();

    private static void AssertAutoRebind(Fixture fixture) =>
        Assert.Equal($"OBLIGATION_AUTO_REBIND goal={fixture.Goal.Id.Value} from={fixture.Bound} to={fixture.Candidate} reason=integration-only",
            Assert.Single(AutoRebindEntries(fixture)));

    private static (BatchLoopSummary Summary, int LandCalls, int AcceptanceRuns) RunLoop(Fixture fixture)
    {
        var landCalls = 0;
        var acceptanceRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return new AcceptanceVerificationSummary(true, [], BranchHeadSha: fixture.Candidate);
            },
            resolveAcceptanceHeads: _ => (fixture.Candidate, ReadGit(fixture.Repository, "rev-parse", "main")),
            getLandingFileScopes: _ => [GuardedPath, GoalPath, SourceSizeRatchet.SourcePath],
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

    private static Fixture CreateFixture(string secondMerge = "writer", int boundCeiling = 20)
    {
        var repository = CreateGitRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(repository, "src"));
            File.WriteAllLines(Path.Combine(repository, GuardedPath), Enumerable.Range(1, 20).Select(i => $"// line {i}"));
            WriteAuthority(repository, 20);
            ReadGit(repository, "add", GuardedPath, SourceSizeRatchet.SourcePath);
            ReadGit(repository, "commit", "-m", "Seed guarded authority");
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/goal.txt and shrink guarded file");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Full acceptance passes", "Goal content preserved"], VerificationClass.TestVerifiable, [], []));
            var worktree = GoalWorktrees.Ensure(repository, goal.Id);
            File.WriteAllLines(Path.Combine(worktree, GuardedPath), Enumerable.Range(6, 15).Select(i => $"// line {i}"));
            File.WriteAllText(Path.Combine(worktree, GoalPath), "goal owned line\n");
            if (boundCeiling != 20) WriteAuthority(worktree, boundCeiling);
            ReadGit(worktree, "add", GuardedPath, GoalPath, SourceSizeRatchet.SourcePath);
            ReadGit(worktree, "commit", "-m", "Goal changes");
            var bound = ReadGit(worktree, "rev-parse", "HEAD");
            for (var index = 0; index < 2; index++)
                kernel.MapCriterionEvidenceOwner(goal.Id, index, 1, CriterionEvidenceOwner.Acceptance,
                    "reviewer", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: bound);

            File.AppendAllLines(Path.Combine(repository, GuardedPath), Enumerable.Range(1, 3).Select(i => $"// main added {i}"));
            ReadGit(repository, "add", GuardedPath);
            ReadGit(repository, "commit", "-m", "Grow guarded file on main");
            var first = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repository, goal.Id);
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, first.Status);
            Assert.Equal("merged-main", first.Detail);
            Assert.Equal(SourceSizeRatchetRetightener.CommitSubject, ReadGit(worktree, "log", "-1", "--format=%s"));
            Assert.Equal(18, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(worktree)!).MaximumLineCount);
            if (secondMerge == "raise")
            {
                var before = File.ReadAllText(Path.Combine(worktree, SourceSizeRatchet.SourcePath));
                WriteAuthority(worktree, 25);
                Assert.True(SourceSizeRatchetRetightener.IsNumericRetightenOnly(before,
                    File.ReadAllText(Path.Combine(worktree, SourceSizeRatchet.SourcePath))));
                ReadGit(worktree, "add", SourceSizeRatchet.SourcePath);
                ReadGit(worktree, "commit", "-m", SourceSizeRatchetRetightener.CommitSubject);
            }
            AppendCommit(repository, "src/unrelated.txt", "second main advance");
            var subject = $"{MergeSubjectPrefix}{GoalWorktrees.BranchName(goal.Id)} before acceptance";
            if (secondMerge == "writer")
            {
                var second = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repository, goal.Id);
                Assert.Equal(GoalWorktreeRebaseStatus.Rebased, second.Status);
                Assert.Equal("merged-main", second.Detail);
            }
            else
            {
                ReadGit(worktree, "merge", "--no-ff", "--no-commit", "main");
                if (secondMerge == "goal-edit")
                {
                    File.WriteAllText(Path.Combine(worktree, GoalPath), "edited goal line\n");
                    ReadGit(worktree, "add", GoalPath);
                }
                if (secondMerge == "foreign") subject = "Integrate main into goal/another before acceptance";
                if (secondMerge == "dispatch") subject = $"{MergeSubjectPrefix}{GoalWorktrees.BranchName(goal.Id)} before Developer dispatch";
                ReadGit(worktree, "commit", "-m", subject);
            }
            var candidate = ReadGit(worktree, "rev-parse", "HEAD");
            Assert.Equal(subject, ReadGit(worktree, "log", "-1", "--format=%s"));
            var walked = ReadGit(worktree, "rev-list", "--first-parent", $"{bound}..{candidate}")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(secondMerge == "raise" ? 4 : 3, walked.Length);
            Assert.Equal(3, ReadGit(worktree, "rev-list", "--parents", "-n", "1", candidate).Split(' ').Length);
            return new Fixture(repository, worktree, kernel, goal, bound, candidate, walked);
        }
        catch { TryDeleteDirectory(repository); throw; }
    }

    private static void WriteAuthority(string root, int ceiling)
    {
        var path = Path.Combine(root, SourceSizeRatchet.SourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"// authority comment\nnew SourceSizeCeiling(\"{GuardedPath}\", {ceiling}), // row comment\n");
    }

    private sealed class Fixture(string repository, string worktree, AgentOrchestratorKernel kernel,
        Goal goal, string bound, string candidate, string[] walked) : IDisposable
    {
        public string Repository { get; } = repository;
        public string Worktree { get; } = worktree;
        public AgentOrchestratorKernel Kernel { get; } = kernel;
        public Goal Goal { get; } = goal;
        public string Bound { get; } = bound;
        public string Candidate { get; set; } = candidate;
        public string[] Walked { get; } = walked;
        public void Dispose() => TryDeleteDirectory(Repository);
    }
}
