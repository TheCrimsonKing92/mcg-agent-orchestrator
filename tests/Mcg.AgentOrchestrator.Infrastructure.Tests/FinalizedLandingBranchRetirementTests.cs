using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its SQLite stores, journal, evidence and fake runner.
public sealed class FinalizedLandingBranchRetirementTests : CliCommandTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReclaimedEvidence_FinalizedLanding_ReturnsTip(bool cohort) =>
        Check(cohort, expectTip: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvancedCandidate_ReturnsNullAfterOneAncestryCheck(bool cohort) =>
        Check(cohort, staleCandidate: true);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    public void LandingOffMainOrGitFailure_ReturnsNull(bool cohort, int ancestryExit) =>
        Check(cohort, ancestryExit: ancestryExit);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentIntentCommit_ReturnsNullWithoutGit(bool cohort) =>
        Check(cohort, differentCommit: true);

    [Fact]
    public void PreparedTrain_ReturnsNullWithoutGit() => Check(cohort: false, preparedOnly: true);

    [Fact]
    public void InvalidatedCohort_ReturnsNullWithoutGit() => Check(cohort: true, invalidated: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIntent_ReturnsNullWithoutGit(bool cohort) => Check(cohort, withIntent: false);

    private static void Check(
        bool cohort, bool expectTip = false, bool staleCandidate = false, int ancestryExit = 0,
        bool differentCommit = false, bool preparedOnly = false, bool invalidated = false,
        bool withIntent = true)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var created = kernel.CreateGoal("Retire finalized landing", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel = WithGoalStatus(kernel, created.Id, GoalStatus.Completed);
            var goal = kernel.GetGoal(created.Id);
            var tip = new string('c', 40);
            var landing = new string('b', 40);
            var main = new string('d', 40);
            var candidate = staleCandidate ? new string('e', 40) : tip.ToUpperInvariant();
            var intentCommit = differentCommit ? new string('f', 40) : landing.ToUpperInvariant();
            if (withIntent)
                GoalOperationJournal.RecordLandingIntent(root, goal, GoalWorktrees.BranchName(goal.Id), "main", intentCommit, "test");
            var trainStore = new MergeTrainAcceptanceStore(Path.Combine(root, "train.db"));
            var cohortStore = new CohortAcceptanceStore(Path.Combine(root, "cohort.db"));
            var sourceTrx = Path.Combine(root, "pass.trx");
            File.WriteAllText(sourceTrx, """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
                  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                </TestRun>
                """);
            IReadOnlyList<string> capturedPaths;
            if (cohort)
            {
                AcceptanceCohortMemberBinding Member(GoalId id, string file) => new(
                    id, candidate, candidate, [file], [file], ChangeRiskTier.Behavior,
                    ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString());
                var identity = AcceptanceCohortIdentity.Create(
                    [Member(goal.Id, "src/A.cs"), Member(GoalId.New(), "src/B.cs")], main, main, "manifest-v1");
                var receipt = cohortStore.SaveGateReceipt(new AcceptanceCohortReceipt(
                    "receipt", identity, AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow,
                    10, [], 0, [sourceTrx], ValidForLanding: true));
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                cohortStore.PrepareLanding(receipt, landing);
                Assert.Equal(2, cohortStore.FinalizeLanding(identity.Value, receipt.ReceiptId).Count);
                cohortStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
                Assert.Single(cohortStore.ReadFinalizedLandingsForGoal(goal.Id));
                if (invalidated)
                    cohortStore.InvalidateLanding(identity.Value, AcceptanceCohortInvalidationReason.EvidenceUnavailable, "test invalidation");
                capturedPaths = receipt.GateTestResultPaths;
            }
            else
            {
                MergeTrainMemberBinding Member(GoalId id, string file) => new MergeTrainMemberBinding(
                    id, candidate, candidate, [file], [file], ChangeRiskTier.Behavior,
                    ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString())
                    .WithRebasedHead(candidate);
                var identity = MergeTrainIdentity.Create(
                    [Member(goal.Id, "src/A.cs"), Member(GoalId.New(), "src/B.cs")], main, main, "manifest-v1");
                var receipt = trainStore.SaveGateReceipt(new MergeTrainReceipt(
                    "receipt", identity, MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow,
                    10, [], 0, [sourceTrx], ValidForLanding: true));
                Assert.True(receipt.HasAuthoritativeLandingEvidence);
                trainStore.PrepareLanding(receipt, landing, null);
                if (!preparedOnly)
                {
                    Assert.Equal(2, trainStore.FinalizeLanding(identity.Value, receipt.ReceiptId).Count);
                    trainStore.CompleteLandingEffects(identity.Value, receipt.ReceiptId);
                    Assert.Single(trainStore.ReadFinalizedLandingsForGoal(goal.Id));
                }
                capturedPaths = receipt.GateTestResultPaths;
            }

            Assert.NotEmpty(capturedPaths);
            foreach (var path in capturedPaths)
            {
                Assert.NotEqual(sourceTrx, path);
                Assert.True(File.Exists(path));
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            Assert.True(File.Exists(sourceTrx));
            Assert.Empty(trainStore.ReadPassedReceiptsForGoal(goal.Id));
            Assert.Empty(cohortStore.ReadPassedReceiptsForGoal(goal.Id));
            if (preparedOnly || invalidated)
            {
                Assert.Empty(trainStore.ReadFinalizedLandingsForGoal(goal.Id));
                Assert.Empty(cohortStore.ReadFinalizedLandingsForGoal(goal.Id));
            }

            var commands = new List<string[]>();
            GitCli.GitResult RunGit(string directory, IReadOnlyList<string> args)
            {
                Assert.Equal(root, directory);
                commands.Add(args.ToArray());
                Assert.Equal(new[] { "merge-base", "--is-ancestor", intentCommit, main }, args);
                return new(ancestryExit, "", ancestryExit > 1 ? "git failure" : "");
            }
            var facts = new GoalBranchFacts(true, true, false, true, true, false, GoalBranchContentState.AbsentFromMain);
            var actual = LandedGoalBranchRetirement.TryGetLandedBranchTip(
                goal, facts, tip, main, GoalOperationJournal.Read(root, goal.Id), trainStore, cohortStore,
                root, RunGit, out var proof);

            Assert.Equal(withIntent && !differentCommit && !preparedOnly && !invalidated ? 1 : 0, commands.Count);
            if (expectTip)
            {
                Assert.Equal(tip, actual);
                Assert.NotNull(proof);
                Assert.Equal(cohort ? "cohort-receipt" : "merge-train-receipt", proof.ReceiptKind);
                Assert.Equal("receipt", proof.ReceiptId);
                Assert.Equal(tip, proof.CandidateRevision);
                Assert.Equal(intentCommit, proof.LandingCommit);
            }
            else
            {
                Assert.Null(actual);
                Assert.Null(proof);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
