using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCriterionEvidenceRecoveryTestsLandingCarry : CliCommandTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AlreadyLandedRebaseRecoversOnlyWithCompletedCarryRecord(bool writeCarry)
    {
        var root = CreateAcceptanceRepository();
        GoalId? goalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Recover landed rebase", [task]);
            goalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "recovered.txt"), "goal work");
            RunGit(worktree, "add", "recovered.txt");
            RunGit(worktree, "commit", "-m", "Reviewed candidate A");
            var oldHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            kernel.RecordTaskVerification(goal.Id, task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", worktree, DateTimeOffset.UtcNow));
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
                goal.Objective, ["Full gate passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: oldHead);

            File.WriteAllText(Path.Combine(root, "main-only.txt"), "main work");
            RunGit(root, "add", "main-only.txt");
            RunGit(root, "commit", "-m", "Unrelated main commit");
            RunGit(worktree, "rebase", "main");
            var newHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(oldHead, newHead);
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(root, oldHead, newHead,
                out var equivalence, out var refusal), refusal);
            var obligation = Assert.Single(goal.CriterionEvidenceObligations);
            if (writeCarry)
                GoalOperationJournal.Completed(root, goal, "conductor:criterion-evidence-landing-carry",
                    $"obligations={obligation.Id}; oldHeads={oldHead}; newHead={newHead}; equivalence={oldHead}: {equivalence}");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            File.WriteAllText(Path.Combine(root, "later-main.txt"), "later work");
            RunGit(root, "add", "later-main.txt");
            RunGit(root, "commit", "-m", "Main advances after landing");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            SavePassedReceipt(root, goal.Id, newHead, store);
            var result = TerminalGoalSweep.Run(
                kernel, root, goal.Id,
                gitRunner: static (directory, args) => GitCli.Run(directory, args.ToArray()),
                orchestratorDirectory: workspace.OrchestratorDirectory,
                mergeTrainAcceptanceStore: store);

            Assert.Equal(writeCarry ? GoalStatus.Completed : GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Equal(writeCarry ? CriterionEvidenceState.Satisfied : CriterionEvidenceState.Pending,
                obligation.State);
            Assert.Equal(writeCarry ? newHead : oldHead, obligation.ExpectedCandidateSha);
            if (writeCarry)
            {
                Assert.Equal(newHead, obligation.CandidateSha);
                Assert.Contains(Assert.Single(result.Goals).Repairs, repair =>
                    repair.Evidence.Contains("recovery=landing-carry-record", StringComparison.Ordinal));
            }
            else
            {
                Assert.Null(obligation.CandidateSha);
            }
        }
        finally
        {
            CleanupAcceptanceRepository(root, goalId);
        }
    }

    private static void SavePassedReceipt(
        string root, GoalId goalId, string candidate, MergeTrainAcceptanceStore store)
    {
        var members = new[]
        {
            Member(goalId, candidate, "src/Recovered.cs", "resource:recovered"),
            Member(GoalId.New(), candidate, "tests/Other.cs", "resource:other")
        };
        var main = RunGitOutput(root, "rev-parse", "main").Trim();
        var tree = RunGitOutput(root, "rev-parse", "main^{tree}").Trim();
        var identity = MergeTrainIdentity.Create(members, main, tree, "manifest-v1");
        var trx = Path.Combine(root, "landing-carry.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testId="1" testName="Passes" outcome="Passed" /></Results>
              <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        store.SaveGateReceipt(new MergeTrainReceipt(
            "landing-carry-receipt", identity, MergeTrainGateOutcome.Passed,
            DateTimeOffset.UtcNow, 10, [], 0, [trx], ValidForLanding: true));
    }

    private static MergeTrainMemberBinding Member(
        GoalId goalId, string candidate, string path, string resource) =>
        new MergeTrainMemberBinding(
            goalId, candidate, candidate, [path], [resource],
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
            GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString())
        .WithRebasedHead(candidate);
}
