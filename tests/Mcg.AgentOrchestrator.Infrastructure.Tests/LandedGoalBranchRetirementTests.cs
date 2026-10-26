using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each call owns its journal, SQLite stores, kernel and fake git runner.
public sealed class LandedGoalBranchRetirementTests : CliCommandTestBase
{
    [Fact]
    public void MatchingTrainReceipt_ReturnsTipWithOneAncestryLaunch() => Check(expectTip: true);

    [Fact]
    public void MatchingCohortReceipt_ReturnsTipWithOneAncestryLaunch() => Check(expectTip: true, cohort: true);

    [Fact]
    public void CancelledGoal_ReturnsNull() => Check(status: GoalStatus.Cancelled);

    [Fact]
    public void VerifiedGoal_ReturnsNull() => Check(status: GoalStatus.Verified);

    [Fact]
    public void MissingBranch_ReturnsNull() => Check(changeFacts: facts => facts with { HasGoalBranch = false });

    [Fact]
    public void MergedBranch_ReturnsNull() => Check(changeFacts: facts => facts with { BranchAlreadyLanded = true });

    [Fact]
    public void EquivalentBranch_ReturnsNull() => Check(changeFacts: facts => facts with { ContentState = GoalBranchContentState.EquivalentToMain });

    [Fact]
    public void MissingLandingIntent_ReturnsNull() => Check(withIntent: false);

    [Fact]
    public void TombstonedLandingIntent_ReturnsNull() => Check(tombstoneIntent: true);

    [Fact]
    public void LandingCommitOffMain_ReturnsNull() => Check(ancestryExit: 1);

    [Fact]
    public void AncestryCommandFailure_ReturnsNull() => Check(ancestryExit: 128);

    [Fact]
    public void StaleReceiptCandidate_ReturnsNull() => Check(staleCandidate: true);

    [Fact]
    public void NoPassedReceipt_ReturnsNull() => Check(withReceipt: false);

    [Fact]
    public void FailedReceipt_ReturnsNull() => Check(passedReceipt: false);

    [Fact]
    public void MissingTip_ReturnsNull() => Check(withTip: false);

    [Fact]
    public void MissingMainRevision_ReturnsNull() => Check(withMain: false);

    private static void Check(
        bool expectTip = false, bool cohort = false, GoalStatus status = GoalStatus.Completed,
        Func<GoalBranchFacts, GoalBranchFacts>? changeFacts = null,
        bool withIntent = true, bool tombstoneIntent = false, int ancestryExit = 0,
        bool staleCandidate = false, bool withReceipt = true, bool passedReceipt = true,
        bool withTip = true, bool withMain = true)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var created = kernel.CreateGoal("Retire proven branch", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel = WithGoalStatus(kernel, created.Id, status);
            var goal = kernel.GetGoal(created.Id);
            var tip = new string('1', 40);
            var landing = new string('2', 40);
            var main = new string('3', 40);
            if (withIntent)
                GoalOperationJournal.RecordLandingIntent(root, goal, GoalWorktrees.BranchName(goal.Id), "main", landing, "test");
            if (tombstoneIntent)
                GoalOperationJournal.TombstoneLandingIntent(root, goal, "intent revoked");
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(root, "train.db"));
            var cohortStore = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var candidate = staleCandidate ? new string('4', 40) : tip;
            if (withReceipt)
                SaveReceipt(root, goal.Id, candidate, main, trainStore, cohortStore, cohort, passedReceipt);
            var facts = new GoalBranchFacts(true, false, true, true, true, false, GoalBranchContentState.AbsentFromMain);
            var commands = new List<string[]>();
            GitCli.GitResult RunGit(string directory, IReadOnlyList<string> args)
            {
                Assert.Equal(root, directory);
                commands.Add(args.ToArray());
                Assert.Equal(new[] { "merge-base", "--is-ancestor", landing, main }, args);
                return new(ancestryExit, "", ancestryExit > 1 ? "git failure" : "");
            }

            var actual = LandedGoalBranchRetirement.TryGetLandedBranchTip(
                goal, changeFacts?.Invoke(facts) ?? facts, withTip ? tip : null, withMain ? main : null,
                GoalOperationJournal.Read(root, goal.Id), trainStore, cohortStore, root, RunGit, out var proof);

            Assert.InRange(commands.Count, 0, 1);
            if (expectTip)
            {
                Assert.Equal(tip, actual);
                Assert.Single(commands);
                Assert.NotNull(proof);
                Assert.Equal(cohort ? "cohort-receipt" : "merge-train-receipt", proof.ReceiptKind);
                Assert.Equal("receipt", proof.ReceiptId);
                Assert.Equal(tip, proof.CandidateRevision);
                Assert.Equal(landing, proof.LandingCommit);
            }
            else
            {
                Assert.Null(actual);
                Assert.Null(proof);
                if (ancestryExit != 0)
                    Assert.Single(commands);
                else
                    Assert.Empty(commands);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void SaveReceipt(
        string root, GoalId goalId, string candidate, string main,
        MergeTrainAcceptanceStore trainStore, CohortAcceptanceStore cohortStore, bool cohort, bool passed)
    {
        var trx = Path.Combine(root, "pass.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        var other = GoalId.New();
        if (cohort)
        {
            AcceptanceCohortMemberBinding Member(GoalId id, string path) => new(
                id, candidate, candidate, [path], [path], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString());
            var identity = AcceptanceCohortIdentity.Create([Member(goalId, "src/A.cs"), Member(other, "src/B.cs")], main, main, "manifest-v1");
            cohortStore.SaveGateReceipt(new AcceptanceCohortReceipt("receipt", identity,
                passed ? AcceptanceCohortGateOutcome.Passed : AcceptanceCohortGateOutcome.Failed,
                DateTimeOffset.UtcNow, 10, [], passed ? 0 : 1, [trx], ValidForLanding: passed));
        }
        else
        {
            MergeTrainMemberBinding Member(GoalId id, string path) => new MergeTrainMemberBinding(
                id, candidate, candidate, [path], [path], ChangeRiskTier.Behavior,
                ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString())
                .WithRebasedHead(candidate);
            var identity = MergeTrainIdentity.Create([Member(goalId, "src/A.cs"), Member(other, "src/B.cs")], main, main, "manifest-v1");
            trainStore.SaveGateReceipt(new MergeTrainReceipt("receipt", identity,
                passed ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.Failed,
                DateTimeOffset.UtcNow, 10, [], passed ? 0 : 1, [trx], ValidForLanding: passed));
        }
    }
}
